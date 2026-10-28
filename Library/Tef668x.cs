using System;
using System.Diagnostics;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.I2c;

namespace ImplicateX.TinyCLR.Drivers.Tuner
{
	/// <summary>
	/// TEF668x/TEF6686 tuner driver for TinyCLR using I2C control plus GPIO reset/RDS signaling.
	/// </summary>
	/// <param name="i2cControllerName">Name of the TinyCLR I2C controller (for example, <c>SC20100.I2cBus</c>).</param>
	/// <param name="resPin">GPIO pin connected to the tuner reset input (active-low pulse during initialization).</param>
	/// <param name="rdsPin">GPIO pin connected to the tuner RDS data-available signaling pin.</param>
	public partial class Tef668x
	(
		string i2cControllerName,
		GpioPin resPin,
		GpioPin rdsPin
	)
	{
		private readonly bool enableDebugOutput = true;
		private const byte i2cAddress = 0x64;
		private const int patchChunkBytes = 24;
		private I2cDevice i2CDevice = null!;
		private PatchEngine patchEngine = null!;
		private ushort currentFmFrequency10kHz;

		/// <summary>
		/// Immutable snapshot of one RDS read transaction.
		/// </summary>
		/// <param name="status">Raw RDS status word.</param>
		/// <param name="blockA">RDS block A payload.</param>
		/// <param name="blockB">RDS block B payload.</param>
		/// <param name="blockC">RDS block C payload.</param>
		/// <param name="blockD">RDS block D payload.</param>
		/// <param name="decodeError">Raw decode-error information returned by the tuner.</param>
		/// <param name="timestampUtc">UTC timestamp captured when the frame was read.</param>
		public sealed class RdsFrame
		( 
			ushort status, 
			ushort blockA, 
			ushort blockB, 
			ushort blockC, 
			ushort blockD, 
			ushort decodeError, 
			DateTime timestampUtc
		)
		{
			/// <summary>Raw status bitfield returned by the device.</summary>
			public ushort Status { get; } = status;

			/// <summary>RDS block A (16-bit).</summary>
			public ushort BlockA { get; } = blockA;

			/// <summary>RDS block B (16-bit).</summary>
			public ushort BlockB { get; } = blockB;

			/// <summary>RDS block C (16-bit).</summary>
			public ushort BlockC { get; } = blockC;

			/// <summary>RDS block D (16-bit).</summary>
			public ushort BlockD { get; } = blockD;

			/// <summary>Raw decode error word for the frame.</summary>
			public ushort DecodeError { get; } = decodeError;

			/// <summary>UTC timestamp when this frame was created by the driver.</summary>
			public DateTime TimestampUtc { get; } = timestampUtc;

			/// <summary>Gets whether the status indicates valid RDS data availability.</summary>
			public bool HasData => ( Status & 0x8000 ) != 0;

			/// <summary>Gets whether data loss is indicated by the tuner.</summary>
			public bool HasDataLoss => ( Status & 0x4000 ) != 0;

			/// <summary>Gets whether this frame marks first PI data after synchronization/restart.</summary>
			public bool IsFirstPiData => ( Status & 0x2000 ) != 0;

			/// <summary>Gets whether the RDS decoder is synchronized.</summary>
			public bool IsSynchronized => ( Status & 0x0200 ) != 0;
		}

		/// <summary>
		/// Defines module identifier constants for TEF6686 functional blocks.
		/// </summary>
		/// <remarks>Use these identifiers when building commands for FM reception, AM reception, audio processing,
		/// and system/application control.</remarks>
		private sealed class Module
		{
			public const byte FMRadioReception = 0x20;
			public const byte AMRadioReception = 0x21;
			public const byte AudioProcessing = 0x30;
			public const byte SystemAndApplicationControl = 0x40;
		}


		/// <summary>
		/// Represents the device's operating state.
		/// </summary>
		/// <remarks>Uses an underlying type of <see cref="ushort"/> to map protocol-defined status codes to named
		/// states.</remarks>
		private enum OperationStatus : ushort
		{
			Boot = 0,
			Idle = 1,
			ActiveStandby = 2,
			ActiveFm = 3,
			ActiveAm = 4
		}

		public enum SeekDirection
		{
			Up,
			Down
		}

		/// <summary>
		/// Performs device startup and transitions the tuner into an active state.
		/// </summary>
		/// <remarks>
		/// Sequence:
		/// <list type="number">
		/// <item>Hardware reset pulse via <c>resPin</c>.</item>
		/// <item>I2C probe.</item>
		/// <item>State-dependent boot flow (patch/start/activate when required).</item>
		/// <item>Optional identification read for diagnostics.</item>
		/// </list>
		/// </remarks>
		/// <exception cref="Exception">
		/// Thrown when probing, status polling, or mandatory state transitions fail.
		/// </exception>
		public void Initialize()
		{
			Debug.WriteLine( "TEF6686 init: open GPIO pins" );

			Debug.WriteLine( "TEF6686 init: hardware reset pulse" );
			resPin.Write( GpioPinValue.Low );
			Thread.Sleep( 5 );
			resPin.Write( GpioPinValue.High );
			Thread.Sleep( 5 );

			Debug.WriteLineIf( enableDebugOutput, $"TEF6686 init: probe I2C address 0x{i2cAddress:X2}" );

			if( !TryOpenDevice( i2cControllerName: i2cControllerName, address: i2cAddress ) )
			{
				throw new Exception( $"TEF6686 I2C device not found at address 0x{i2cAddress:X2} on controller {i2cControllerName}" );
			}

			if( !TryGetOperationStatus( out ushort currentStatus ) )
			{
				throw new Exception( "TEF6686 operation status read failed." );
			}

			Debug.WriteLineIf( enableDebugOutput, $"TEF6686 init: detected state 0x{currentStatus:X4}" );

			if( currentStatus == (ushort)OperationStatus.Boot )
			{
				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: boot state detected, running startup sequence" );
				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: apply patch/LUT" );

				patchEngine = new( this );
				patchEngine.ApplyPatch();

				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: send Start" );

				WriteRawCommand( command: 0x14, 0x0001 );

				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: wait for idle state" );

				if( !WaitForOperationStatus( OperationStatus.Idle, timeoutMilliseconds: 1500 ) )
				{
					throw new Exception( "TEF6686 did not enter idle state after Start command." );
				}

				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: send Activate mode=1" );
				WriteSetCommand( Module.SystemAndApplicationControl, command: 0x05, index: 0x01, 0x0001 );

				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: wait for active standby state" );

				if( !WaitForOperationStatus( OperationStatus.ActiveStandby, timeoutMilliseconds: 2000 ) )
				{
					throw new Exception( "TEF6686 did not enter active standby state after Activate command." );
				}
			}
			else if( currentStatus == (ushort)OperationStatus.Idle )
			{
				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: idle state detected, skipping patch/start" );
				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: send Activate mode=1" );

				WriteSetCommand( Module.SystemAndApplicationControl, command: 0x05, index: 0x01, 0x0001 );

				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: wait for active state" );

				if( !WaitForOperationStatus( OperationStatus.ActiveStandby, timeoutMilliseconds: 2000 ) )
				{
					throw new Exception( "TEF6686 did not enter active state after Activate command." );
				}
			}
			else if( IsActiveState( currentStatus ) )
			{
				Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: active state already present, skipping startup sequence" );
			}
			else
			{
				throw new Exception( $"TEF6686 unsupported operation status 0x{currentStatus:X4}" );
			}

			Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: read identification" );

			if( TryGetIdentification( out ushort deviceId, out ushort hwVersion, out ushort swVersion ) )
			{
				Debug.WriteLineIf( enableDebugOutput, $"TEF6686 identification: device=0x{deviceId:X4}, hw=0x{hwVersion:X4}, sw=0x{swVersion:X4}" );
			}
			else
			{
				Debug.WriteLineIf( enableDebugOutput, "TEF6686 identification read failed" );
			}

			Debug.WriteLineIf( enableDebugOutput, "TEF6686 init: startup sequence complete" );
		}

		/// <summary>
		/// Determines whether an operation status value represents an active state.
		/// </summary>
		/// <param name="status">Operation status value to evaluate.</param>
		/// <returns><see langword="true"/> when <paramref name="status"/> is ActiveStandby, ActiveFm, or ActiveAm; otherwise, <see
		/// langword="false"/>.</returns>
		private bool IsActiveState( ushort status )
		{
			return status == (ushort)OperationStatus.ActiveStandby
				|| status == (ushort)OperationStatus.ActiveFm
				|| status == (ushort)OperationStatus.ActiveAm;
		}

		/// <summary>
		/// Waits for the operation status to reach the expected value within the specified timeout.
		/// </summary>
		/// <param name="expectedStatus">The expected operation status.</param>
		/// <param name="timeoutMilliseconds">The timeout in milliseconds.</param>
		/// <returns><see langword="true"/> if the expected status is reached within the timeout; otherwise <see langword="false"/>.</returns>
		private bool WaitForOperationStatus( OperationStatus expectedStatus, int timeoutMilliseconds )
		{
			DateTime deadline = DateTime.UtcNow.AddMilliseconds( timeoutMilliseconds );
			int lastStatus = -1;

			while( DateTime.UtcNow <= deadline )
			{
				if( TryGetOperationStatus( out ushort status ) )
				{
					if( lastStatus != status )
					{
						Debug.WriteLine( $"TEF6686 status: 0x{status:X4}" );
						lastStatus = status;
					}

					if( status == (ushort)expectedStatus
						|| ( expectedStatus == OperationStatus.ActiveStandby && IsActiveState( status ) ) )
					{
						Debug.WriteLineIf( enableDebugOutput, $"TEF6686 status reached: {expectedStatus} (0x{status:X4})" );
						return true;
					}
				}

				Thread.Sleep( 10 );
			}

			Debug.WriteLineIf( enableDebugOutput, $"TEF6686 status timeout waiting for {expectedStatus}" );
			return false;
		}

		/// <summary>
		/// Reads the current operation status from the system and application control module.
		/// </summary>
		/// <param name="status">When this method returns <see langword="true"/>, contains the current operation status; otherwise <c>0</c>.</param>
		/// <returns><see langword="true"/> if the status is read successfully; otherwise <see langword="false"/>.</returns>
		private bool TryGetOperationStatus( out ushort status )
		{
			status = 0;
			byte[] writeBuffer = [ Module.SystemAndApplicationControl, 0x80, 0x01 ];
			byte[] readBuffer = new byte[ 2 ];

			try
			{
				I2cTransferResult result = i2CDevice.WriteReadPartial( writeBuffer, readBuffer );

				if( result.Status == I2cTransferStatus.FullTransfer )
				{
					status = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
					return true;
				}
			}
			catch
			{
			}

			return false;
		}

		/// <summary>
		/// Reads device identification words.
		/// </summary>
		/// <param name="deviceId">Returned silicon/device identifier.</param>
		/// <param name="hwVersion">Returned hardware version word.</param>
		/// <param name="swVersion">Returned software/firmware version word.</param>
		/// <returns>
		/// <see langword="true"/> when all bytes were read successfully; otherwise <see langword="false"/>.
		/// </returns>
		public bool TryGetIdentification( out ushort deviceId, out ushort hwVersion, out ushort swVersion )
		{
			deviceId = 0;
			hwVersion = 0;
			swVersion = 0;
			byte[] writeBuffer = [ Module.SystemAndApplicationControl, 0x82, 0x01 ];
			byte[] readBuffer = new byte[ 6 ];

			try
			{
				I2cTransferResult result = i2CDevice.WriteReadPartial( writeBuffer, readBuffer );

				if( result.Status == I2cTransferStatus.FullTransfer )
				{
					deviceId = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
					hwVersion = (ushort)( ( readBuffer[ 2 ] << 8 ) | readBuffer[ 3 ] );
					swVersion = (ushort)( ( readBuffer[ 4 ] << 8 ) | readBuffer[ 5 ] );
					return true;
				}
			}
			catch
			{
			}

			return false;
		}

		/// <summary>
		/// Enables or disables audio muting in the audio processing module.
		/// </summary>
		/// <param name="mute"><see langword="true"/> to mute audio; <see langword="false"/> to unmute.</param>
		public void SetAudioMute( bool mute )
		{
			ushort mode = mute ? (ushort)1 : (ushort)0;
			WriteSetCommand( Module.AudioProcessing, command: 0x0B, index: 0x01, mode );
			Debug.WriteLineIf( enableDebugOutput, $"TEF6686 audio mute: {( mute ? "on" : "off" )}" );
		}

		/// <summary>
		/// Tunes the FM receiver to a specified frequency.
		/// </summary>
		/// <param name="frequency10kHz">
		/// Target FM frequency in 10 kHz units (e.g., 10170 = 101.70 MHz).
		/// </param>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Thrown when frequency is outside 65.00 MHz to 108.00 MHz.
		/// </exception>
		public void TuneToFm( ushort frequency10kHz )
		{
			if( frequency10kHz < 6500 || frequency10kHz > 10800 )
			{
				throw new ArgumentOutOfRangeException( nameof( frequency10kHz ), "FM frequency must be in 10 kHz units between 6500 and 10800." );
			}

			WriteSetCommand( Module.FMRadioReception, command: 0x01, index: 0x01, 0x0001, frequency10kHz );
			currentFmFrequency10kHz = frequency10kHz;
			Debug.WriteLineIf( enableDebugOutput, $"TEF6686 FM tune: {frequency10kHz / 100}.{frequency10kHz % 100:D2} MHz" );
		}

		/// <summary>
		/// Seeks the next FM station in the specified direction within the provided frequency range and optional RDS
		/// synchronization requirement.
		/// </summary>
		/// <remarks>The search wraps within the specified range and validates station quality in two consecutive
		/// checks before confirming a hit.</remarks>
		/// <param name="direction">Search direction to move through the FM band.</param>
		/// <param name="foundFrequency10kHz">Receives the frequency of the station found, in 10 kHz units; set to 0 when no station is found.</param>
		/// <param name="requireRdsSync">Requires RDS synchronization before accepting a station.</param>
		/// <param name="minFrequency10kHz">Lower frequency bound, in 10 kHz units.</param>
		/// <param name="maxFrequency10kHz">Upper frequency bound, in 10 kHz units.</param>
		/// <param name="step10kHz">Frequency increment per seek step, in 10 kHz units.</param>
		/// <param name="minQualityStatus">Minimum quality status threshold a candidate station must meet.</param>
		/// <param name="settleMilliseconds">Delay after tuning each candidate frequency before evaluating status values.</param>
		/// <param name="maxSteps">Maximum number of candidate frequencies to evaluate before stopping.</param>
		/// <param name="rdsSyncTimeoutMilliseconds">Maximum time to wait for RDS synchronization when required.</param>
		/// <returns>true if a station meeting the criteria is found; otherwise, false.</returns>
		/// <exception cref="ArgumentOutOfRangeException">Thrown when the frequency range is outside 65.00 to 108.00 MHz, when the minimum frequency is not less than the
		/// maximum frequency, or when the seek step is 0.</exception>
		public bool SeekNextStation(
			SeekDirection direction,
			out ushort foundFrequency10kHz,
			bool requireRdsSync = false,
			ushort minFrequency10kHz = 8700,
			ushort maxFrequency10kHz = 10800,
			ushort step10kHz = 10,
			ushort minQualityStatus = 0x0100,
			int settleMilliseconds = 45,
			int maxSteps = 450,
			int rdsSyncTimeoutMilliseconds = 1200 )
		{
			foundFrequency10kHz = 0;

			if( minFrequency10kHz < 6500 || maxFrequency10kHz > 10800 || minFrequency10kHz >= maxFrequency10kHz )
				throw new ArgumentOutOfRangeException( nameof( minFrequency10kHz ), "FM seek range must stay within 65.00 to 108.00 MHz and min < max." );

			if( step10kHz == 0 )
				throw new ArgumentOutOfRangeException( nameof( step10kHz ), "Seek step must be greater than zero." );

			if( settleMilliseconds < 5 )
				settleMilliseconds = 5;

			if( maxSteps < 1 )
				maxSteps = 1;

			bool hasCurrent = currentFmFrequency10kHz >= minFrequency10kHz && currentFmFrequency10kHz <= maxFrequency10kHz;
			ushort candidate = hasCurrent
				? StepFrequency( currentFmFrequency10kHz, direction, minFrequency10kHz, maxFrequency10kHz, step10kHz )
				: ( direction == SeekDirection.Up ? minFrequency10kHz : maxFrequency10kHz );

			for( int i = 0; i < maxSteps; i++ )
			{
				TuneToFm( candidate );
				Thread.Sleep( settleMilliseconds );

				if( !TryGetFmQualityStatus( out ushort qualityStatus ) || !TryGetFmSignalStatus( out ushort signalStatus ) )
				{
					candidate = StepFrequency( candidate, direction, minFrequency10kHz, maxFrequency10kHz, step10kHz );
					continue;
				}

				if( !IsLikelySeekCandidate( qualityStatus, signalStatus, minQualityStatus ) )
				{
					candidate = StepFrequency( candidate, direction, minFrequency10kHz, maxFrequency10kHz, step10kHz );
					continue;
				}

				Thread.Sleep( 15 );

				if( !TryGetFmQualityStatus( out ushort confirmQualityStatus ) || !TryGetFmSignalStatus( out ushort confirmSignalStatus ) )
				{
					candidate = StepFrequency( candidate, direction, minFrequency10kHz, maxFrequency10kHz, step10kHz );
					continue;
				}

				if( !IsLikelySeekCandidate( confirmQualityStatus, confirmSignalStatus, minQualityStatus ) )
				{
					candidate = StepFrequency( candidate, direction, minFrequency10kHz, maxFrequency10kHz, step10kHz );
					continue;
				}

				if( requireRdsSync && !WaitForRdsSync( rdsSyncTimeoutMilliseconds ) )
				{
					candidate = StepFrequency( candidate, direction, minFrequency10kHz, maxFrequency10kHz, step10kHz );
					continue;
				}

				foundFrequency10kHz = candidate;
				Debug.WriteLineIf( enableDebugOutput, $"TEF6686 seek hit: {foundFrequency10kHz / 100}.{foundFrequency10kHz % 100:D2} MHz" );
				return true;
			}

			Debug.WriteLineIf( enableDebugOutput, "TEF6686 seek: no station found." );
			return false;
		}

		/// <summary>
		/// Waits until an RDS frame reports synchronization or the timeout interval expires.
		/// </summary>
		/// <remarks>Polls RDS status at short intervals until the deadline is reached.</remarks>
		/// <param name="timeoutMilliseconds">Maximum time to wait, in milliseconds.</param>
		/// <returns><see langword="true"/> if synchronization is detected before the timeout; otherwise, <see langword="false"/>.</returns>
		private bool WaitForRdsSync( int timeoutMilliseconds )
		{
			DateTime deadline = DateTime.UtcNow.AddMilliseconds( timeoutMilliseconds );

			while( DateTime.UtcNow <= deadline )
			{
				if( TryGetRdsStatus( out RdsFrame frame ) && frame.IsSynchronized )
				{
					return true;
				}

				Thread.Sleep( 40 );
			}

			return false;
		}

		/// <summary>
		/// Determines whether the current status indicates a likely seek candidate.
		/// </summary>
		/// <param name="qualityStatus">Current quality metric to evaluate.</param>
		/// <param name="signalStatus">Current signal status flags; bits masked by 0xC000 are treated as seek indicators.</param>
		/// <param name="minQualityStatus">Minimum quality threshold required to qualify directly.</param>
		/// <returns>true if qualityStatus meets or exceeds minQualityStatus, or if either signal flag in 0xC000 is set; otherwise,
		/// false.</returns>
		private static bool IsLikelySeekCandidate( ushort qualityStatus, ushort signalStatus, ushort minQualityStatus )
		{
			if( qualityStatus >= minQualityStatus )
				return true;

			return ( signalStatus & 0xC000 ) != 0;
		}

		/// <summary>
		/// Calculates the next frequency by stepping up or down and wrapping to the opposite band edge when the limit is
		/// exceeded.
		/// </summary>
		/// <param name="frequency10kHz">Current frequency in 10 kHz units.</param>
		/// <param name="direction">Direction to step the frequency.</param>
		/// <param name="minFrequency10kHz">Lower band limit in 10 kHz units.</param>
		/// <param name="maxFrequency10kHz">Upper band limit in 10 kHz units.</param>
		/// <param name="step10kHz">Step size in 10 kHz units.</param>
		/// <returns>The stepped frequency in 10 kHz units, wrapped to the band minimum when stepping above the maximum, or to the band
		/// maximum when stepping below the minimum.</returns>
		private static ushort StepFrequency( ushort frequency10kHz, SeekDirection direction, ushort minFrequency10kHz, ushort maxFrequency10kHz, ushort step10kHz )
		{
			if( direction == SeekDirection.Up )
			{
				int stepped = frequency10kHz + step10kHz;
				return stepped > maxFrequency10kHz ? minFrequency10kHz : (ushort)stepped;
			}

			int downwardStepped = frequency10kHz - step10kHz;
			return downwardStepped < minFrequency10kHz ? maxFrequency10kHz : (ushort)downwardStepped;
		}

		/// <summary>
		/// Reads FM quality status word.
		/// </summary>
		/// <param name="status">Returned raw quality status word.</param>
		/// <returns><see langword="true"/> on successful transfer; otherwise <see langword="false"/>.</returns>
		public bool TryGetFmQualityStatus( out ushort status )
		{
			status = 0;
			byte[] readBuffer = new byte[ 2 ];

			if( !TryReadCommand( Module.FMRadioReception, command: 0x80, index: 0x01, readBuffer ) )
			{
				return false;
			}

			status = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
			return true;
		}

		/// <summary>
		/// Reads FM signal status word.
		/// </summary>
		/// <param name="status">Returned raw signal status word.</param>
		/// <returns><see langword="true"/> on successful transfer; otherwise <see langword="false"/>.</returns>
		public bool TryGetFmSignalStatus( out ushort status )
		{
			status = 0;
			byte[] readBuffer = new byte[ 2 ];

			if( !TryReadCommand( Module.FMRadioReception, command: 0x85, index: 0x01, readBuffer ) )
			{
				return false;
			}

			status = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
			return true;
		}

		/// <summary>
		/// Configures the RDS decoder mode and RDS output pin interface.
		/// </summary>
		/// <param name="mode">RDS mode (default: 1 = enabled).</param>
		/// <param name="restart">Restart behavior (default: 2 = restart decoder path).</param>
		/// <param name="pinInterface">
		/// RDS pin mode (TEF6686 chapter 3.23): 0 = none, 2 = DAVN (active-low), 4 = RDDA/RDCL legacy 2-wire output.
		/// </param>
		public void SetRds( ushort mode = 1, ushort restart = 2, ushort pinInterface = 2 )
		{
			WriteSetCommand( Module.FMRadioReception, command: 0x51, index: 0x01, mode, restart, pinInterface );
		}

		/// <summary>
		/// Creates an event-driven RDS subsystem bound to the configured RDS GPIO pin.
		/// </summary>
		/// <param name="dataAvailableActiveHigh">
		/// GPIO polarity used by the consumer logic. Keep <see langword="true"/> when hardware wiring/inversion makes
		/// "data available" appear as a high level at the MCU pin.
		/// </param>
		/// <returns>A configured <c>RdsSubSystem</c> instance.</returns>
		public RdsSubSystem CreateRdsSubSystem( bool dataAvailableActiveHigh = true )
		{
			return new RdsSubSystem( this, rdsPin, dataAvailableActiveHigh );
		}

		/// <summary>
		/// Reads the current RDS status frame.
		/// </summary>
		/// <param name="frame">Returned RDS frame.</param>
		/// <returns><see langword="true"/> on successful transfer; otherwise <see langword="false"/>.</returns>
		public bool TryGetRdsStatus( out RdsFrame frame )
		{
			return TryReadRds( command: 0x82, out frame );
		}

		/// <summary>
		/// Reads the current RDS data frame.
		/// </summary>
		/// <param name="frame">Returned RDS frame.</param>
		/// <returns><see langword="true"/> on successful transfer; otherwise <see langword="false"/>.</returns>
		public bool TryGetRdsData( out RdsFrame frame )
		{
			return TryReadRds( command: 0x83, out frame );
		}

		/// <summary>
		/// Reads one 12-byte RDS frame payload from the FM reception module and converts it to an <see cref="RdsFrame"/>.
		/// </summary>
		/// <param name="command">
		/// RDS read command to execute (for example, <c>0x82</c> for status or <c>0x83</c> for data).
		/// </param>
		/// <param name="frame">
		/// When this method returns <see langword="true"/>, contains the parsed RDS frame with status, blocks A-D, decode error, and a UTC timestamp;
		/// otherwise <see langword="null"/>.
		/// </param>
		/// <returns>
		/// <see langword="true"/> if the command transfer succeeds and the frame is parsed; otherwise <see langword="false"/>.
		/// </returns>
		private bool TryReadRds( byte command, out RdsFrame frame )
		{
			frame = null!;
			byte[] readBuffer = new byte[ 12 ];

			if( !TryReadCommand( Module.FMRadioReception, command: command, index: 0x01, readBuffer ) )
			{
				return false;
			}

			ushort status = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
			ushort blockA = (ushort)( ( readBuffer[ 2 ] << 8 ) | readBuffer[ 3 ] );
			ushort blockB = (ushort)( ( readBuffer[ 4 ] << 8 ) | readBuffer[ 5 ] );
			ushort blockC = (ushort)( ( readBuffer[ 6 ] << 8 ) | readBuffer[ 7 ] );
			ushort blockD = (ushort)( ( readBuffer[ 8 ] << 8 ) | readBuffer[ 9 ] );
			ushort decodeError = (ushort)( ( readBuffer[ 10 ] << 8 ) | readBuffer[ 11 ] );

			frame = new RdsFrame( status, blockA, blockB, blockC, blockD, decodeError, DateTime.UtcNow );
			return true;
		}

		/// <summary>
		/// Reads FM processing metrics from the tuner DSP path.
		/// </summary>
		/// <param name="softmute">Soft-mute metric/value.</param>
		/// <param name="highcut">High-cut metric/value.</param>
		/// <param name="stereo">Stereo blend/stereo metric.</param>
		/// <param name="sthiblend">ST-HI blend metric/value.</param>
		/// <returns><see langword="true"/> on successful transfer; otherwise <see langword="false"/>.</returns>
		public bool TryGetFmProcessingStatus( out ushort softmute, out ushort highcut, out ushort stereo, out ushort sthiblend )
		{
			softmute = 0;
			highcut = 0;
			stereo = 0;
			sthiblend = 0;
			byte[] readBuffer = new byte[ 8 ];

			if( !TryReadCommand( Module.FMRadioReception, command: 0x86, index: 0x01, readBuffer ) )
			{
				return false;
			}

			softmute = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
			highcut = (ushort)( ( readBuffer[ 2 ] << 8 ) | readBuffer[ 3 ] );
			stereo = (ushort)( ( readBuffer[ 4 ] << 8 ) | readBuffer[ 5 ] );
			sthiblend = (ushort)( ( readBuffer[ 6 ] << 8 ) | readBuffer[ 7 ] );
			return true;
		}

		/// <summary>
		/// Reads a raw I2C command with a module, command, index, and data words, enforcing complete transfer semantics.
		/// </summary>
		/// <param name="module">Module byte.</param>
		/// <param name="command">Command byte.</param>
		/// <param name="index">Index byte.</param>
		/// <param name="readBuffer">Buffer to store read data.</param>
		/// <returns><see langword="true"/> on successful transfer; otherwise <see langword="false"/>.</returns>
		private bool TryReadCommand( byte module, byte command, byte index, byte[] readBuffer )
		{
			byte[] writeBuffer = [ module, command, index ];

			try
			{
				I2cTransferResult result = i2CDevice.WriteReadPartial( writeBuffer, readBuffer );
				return result.Status == I2cTransferStatus.FullTransfer;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Writes a raw I2C command with a module, command, index, and data words, enforcing complete transfer semantics.
		/// </summary>
		/// <param name="module">Module byte.</param>
		/// <param name="command">Command byte.</param>
		/// <param name="index">Index byte.</param>
		/// <param name="dataWords">Data words to send.</param>
		private void WriteSetCommand( byte module, byte command, byte index, params ushort[] dataWords )
		{
			byte[] buffer = new byte[ 3 + ( dataWords.Length * 2 ) ];
			buffer[ 0 ] = module;
			buffer[ 1 ] = command;
			buffer[ 2 ] = index;

			for( int i = 0; i < dataWords.Length; i++ )
			{
				int offset = 3 + ( i * 2 );
				buffer[ offset ] = (byte)( dataWords[ i ] >> 8 );
				buffer[ offset + 1 ] = (byte)( dataWords[ i ] & 0xFF );
			}

			WriteBuffer( buffer );
		}

		/// <summary>
		/// Writes a raw I2C command and enforces complete transfer semantics.
		/// </summary>
		/// <param name="command">Command byte.</param>
		/// <param name="dataWords">Data words to send.</param>
		private void WriteRawCommand( byte command, params ushort[] dataWords )
		{
			byte[] buffer = new byte[ 1 + ( dataWords.Length * 2 ) ];
			buffer[ 0 ] = command;

			for( int i = 0; i < dataWords.Length; i++ )
			{
				int offset = 1 + ( i * 2 );
				buffer[ offset ] = (byte)( dataWords[ i ] >> 8 );
				buffer[ offset + 1 ] = (byte)( dataWords[ i ] & 0xFF );
			}

			WriteBuffer( buffer );
		}

		/// <summary>
		/// Sends boot/patch payload data in fixed-size chunks aligned to 16-bit words.
		/// </summary>
		/// <param name="command">Boot-stream command byte.</param>
		/// <param name="data">Patch/LUT data payload.</param>
		/// <exception cref="ArgumentException">
		/// Thrown when <paramref name="data"/> length is not aligned to 2-byte words.
		/// </exception>
		private void WriteBootDataStream( byte command, byte[] data )
		{
			if( data.Length % 2 != 0 )
			{
				throw new ArgumentException( "Boot data stream must be aligned to 2-byte words.", nameof( data ) );
			}

			for( int offset = 0; offset < data.Length; offset += patchChunkBytes )
			{
				int chunkLength = Math.Min( patchChunkBytes, data.Length - offset );

				if( ( chunkLength & 1 ) != 0 )
				{
					chunkLength--;
				}

				byte[] buffer = new byte[ 1 + chunkLength ];
				buffer[ 0 ] = command;
				Array.Copy( data, offset, buffer, 1, chunkLength );
				WriteBuffer( buffer );
			}
		}

		/// <summary>
		/// Writes a raw I2C frame and enforces complete transfer semantics.
		/// </summary>
		/// <param name="buffer">Encoded command/data buffer to write.</param>
		/// <exception cref="Exception">Thrown if the write is not a full transfer.</exception>
		private void WriteBuffer( byte[] buffer )
		{
			I2cTransferResult result = i2CDevice.WritePartial( buffer );

			if( result.Status != I2cTransferStatus.FullTransfer )
			{
				throw new Exception( $"TEF6686 I2C write failed: {result.Status}" );
			}
		}

		/// <summary>
		/// Opens and probes the target I2C device address.
		/// </summary>
		/// <param name="i2cControllerName">TinyCLR I2C controller name.</param>
		/// <param name="address">7-bit I2C slave address.</param>
		/// <returns>
		/// <see langword="true"/> if the device can be opened and appears responsive; otherwise <see langword="false"/>.
		/// </returns>
		private bool TryOpenDevice( string i2cControllerName, byte address )
		{
			I2cController i2cController = I2cController.FromName( i2cControllerName );

			try
			{
				this.i2CDevice = i2cController.GetDevice(
					new I2cConnectionSettings( address, I2cMode.Master, I2cAddressFormat.SevenBit, 100_000U ) );

				Debug.WriteLineIf( enableDebugOutput, $"TEF6686 I2C probe 0x{address:X2} succeeded on controller {i2cControllerName}" );
			}
			catch( Exception ex )
			{
				Debug.WriteLineIf( enableDebugOutput, $"TEF6686 I2C probe 0x{address:X2} failed: {ex.Message}" );
				return false;
			}

			try
			{
				byte[] rBuf = new byte[ 1 ];

				if( this.i2CDevice.ReadPartial( rBuf ).Status != I2cTransferStatus.SlaveAddressNotAcknowledged )
				{
					return true;
				}
			}
			catch( Exception ex )
			{
				Debug.WriteLineIf( enableDebugOutput, $"I2C probe failed for address 0x{address:X2}: {ex.Message}" );
			}

			return false;
		}
	}
}
