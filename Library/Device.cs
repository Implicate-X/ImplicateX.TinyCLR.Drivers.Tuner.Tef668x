using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.I2c;

namespace ImplicateX.TinyCLR.Drivers.Tuner.Tef668x
{
	public enum TefGpioOutput : ushort
	{
		Rds = 0x0101,
		Qsi = 0x0102,
		QsiOrRdsActiveLow = 0x0103,
		Agc = 0x0106
	}

	public partial class Device
	(
		string i2cControllerName,
		int resPinID,
		int rdsPinID,
		int tefGpioIndex = 0,
		TefGpioOutput tefGpioOutput = TefGpioOutput.Rds,
		bool enableRdsWatchdog = false
	)
	{
		private const byte i2cAddress = 0x64;
		private const int patchChunkBytes = 24;
		private const int rdsBufferCapacity = 32;
		private const int rdsBurstReadLimit = 1;
		private const int rdsBurstWindowMilliseconds = 120;
		private const int rdsServiceIntervalMilliseconds = 20;
		private const int rdsLowLevelKickIntervalMilliseconds = 500;
		private const int rdsInterReadPauseMilliseconds = 2;
		private const int rdsWatchdogIntervalMilliseconds = 75;
		private const int rdsWatchdogEdgeGraceMilliseconds = 300;
		private const int rdsRequiredPsChars = 6;
		private const int rdsRequiredRtChars = 20;
		private I2cDevice i2CDevice;
		private GpioPin resPin;
		private GpioPin rdsPin;
		private PatchEngine patchEngine;
		private readonly object rdsSync = new();
		private readonly RdsFrame[] rdsFrames = new RdsFrame[ rdsBufferCapacity ];
		private readonly List<string> rdsTextEntries = new();
		private readonly char[] rdsProgramService = [ ' ', ' ', ' ', ' ', ' ', ' ', ' ', ' ' ];
		private readonly bool[] rdsProgramServiceValid = new bool[ 8 ];
		private readonly char[] rdsRadioText = new char[ 64 ];
		private readonly bool[] rdsRadioTextValid = new bool[ 64 ];
		private int rdsWriteIndex;
		private int rdsReadIndex;
		private int rdsFrameCount;
		private ushort rdsPiCode;
		private ushort rdsPty;
		private byte rdsRadioTextAbFlag = 0xFF;
		private bool isReadingRds;
		private bool isRdsHandlerAttached;
		private bool isRdsServiceThreadStarted;
		private bool rdsServiceRequested;
		private bool isRdsWatchdogStarted;
		private DateTime lastRdsEdgeUtc = DateTime.MinValue;

		public sealed class RdsFrame
		{
			public ushort Status { get; }
			public ushort BlockA { get; }
			public ushort BlockB { get; }
			public ushort BlockC { get; }
			public ushort BlockD { get; }
			public ushort DecodeError { get; }
			public DateTime TimestampUtc { get; }

			public bool HasData => ( Status & 0x8000 ) != 0;
			public bool HasDataLoss => ( Status & 0x4000 ) != 0;

			public RdsFrame( ushort status, ushort blockA, ushort blockB, ushort blockC, ushort blockD, ushort decodeError, DateTime timestampUtc )
			{
				Status = status;
				BlockA = blockA;
				BlockB = blockB;
				BlockC = blockC;
				BlockD = blockD;
				DecodeError = decodeError;
				TimestampUtc = timestampUtc;
			}
		}

		private sealed class Module
		{
			public const byte FMRadioReception = 0x20;
			public const byte AMRadioReception = 0x21;
			public const byte AudioProcessing = 0x30;
			public const byte SystemAndApplicationControl = 0x40;
		}

		private enum OperationStatus : ushort
		{
			Boot = 0,
			Idle = 1,
			ActiveStandby = 2,
			ActiveFm = 3,
			ActiveAm = 4
		}

		public void Initialize()
		{
			resPin = GpioController.GetDefault().OpenPin( resPinID );
			rdsPin = GpioController.GetDefault().OpenPin( rdsPinID );

			resPin.SetDriveMode( GpioPinDriveMode.Output );
			rdsPin.SetDriveMode( GpioPinDriveMode.InputPullUp );

			resPin.Write( GpioPinValue.Low );
			Thread.Sleep( 5 );
			resPin.Write( GpioPinValue.High );
			Thread.Sleep( 5 );

			if( !TryOpenDevice( i2cControllerName: i2cControllerName, address: i2cAddress ) )
			{
				throw new Exception( $"TEF6686 I2C device not found at address 0x{i2cAddress:X2} on controller {i2cControllerName}" );
			}

			if( !TryGetOperationStatus( out ushort currentStatus ) )
			{
				throw new Exception( "TEF6686 operation status read failed." );
			}

			if( currentStatus == (ushort)OperationStatus.Boot )
			{
				patchEngine = new( this );
				patchEngine.ApplyPatch();

				WriteRawCommand( command: 0x14, 0x0001 );

				if( !WaitForOperationStatus( OperationStatus.Idle, timeoutMilliseconds: 1500 ) )
				{
					throw new Exception( "TEF6686 did not enter idle state after Start command." );
				}

				WriteSetCommand( Module.SystemAndApplicationControl, command: 0x05, index: 0x01, 0x0001 );

				if( !WaitForOperationStatus( OperationStatus.ActiveStandby, timeoutMilliseconds: 2000 ) )
				{
					throw new Exception( "TEF6686 did not enter active standby state after Activate command." );
				}
			}
			else if( currentStatus == (ushort)OperationStatus.Idle )
			{
				WriteSetCommand( Module.SystemAndApplicationControl, command: 0x05, index: 0x01, 0x0001 );

				if( !WaitForOperationStatus( OperationStatus.ActiveStandby, timeoutMilliseconds: 2000 ) )
				{
					throw new Exception( "TEF6686 did not enter active state after Activate command." );
				}
			}
			else if( !IsActiveState( currentStatus ) )
			{
				throw new Exception( $"TEF6686 unsupported operation status 0x{currentStatus:X4}" );
			}

			if( TryGetIdentification( out ushort deviceId, out ushort hwVersion, out ushort swVersion ) )
			{
				Debug.WriteLine( $"TEF6686 identification: device=0x{deviceId:X4}, hw=0x{hwVersion:X4}, sw=0x{swVersion:X4}" );
			}

			InitializeRdsCapture();
			Debug.WriteLine( "TEF6686 init complete" );
		}

		private bool IsActiveState( ushort status )
		{
			return status == (ushort)OperationStatus.ActiveStandby
				|| status == (ushort)OperationStatus.ActiveFm
				|| status == (ushort)OperationStatus.ActiveAm;
		}

		private bool WaitForOperationStatus( OperationStatus expectedStatus, int timeoutMilliseconds )
		{
			DateTime deadline = DateTime.UtcNow.AddMilliseconds( timeoutMilliseconds );

			while( DateTime.UtcNow <= deadline )
			{
				if( TryGetOperationStatus( out ushort status )
					&& ( status == (ushort)expectedStatus
						|| ( expectedStatus == OperationStatus.ActiveStandby && IsActiveState( status ) ) ) )
				{
					return true;
				}

				Thread.Sleep( 10 );
			}

			Debug.WriteLine( $"TEF6686 status timeout waiting for {expectedStatus}" );
			return false;
		}

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

		public void SetAudioMute( bool mute )
		{
			ushort mode = mute ? (ushort)1 : (ushort)0;
			WriteSetCommand( Module.AudioProcessing, command: 0x0B, index: 0x01, mode );
		}

		public void TuneToFm( ushort frequency10kHz )
		{
			if( frequency10kHz < 6500 || frequency10kHz > 10800 )
			{
				throw new ArgumentOutOfRangeException( nameof( frequency10kHz ), "FM frequency must be in 10 kHz units between 6500 and 10800." );
			}

			WriteSetCommand( Module.FMRadioReception, command: 0x01, index: 0x01, 0x0001, frequency10kHz );
		}

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

		public bool TryGetFmRdsStatus( out ushort status, out bool dataAvailable, out bool dataLoss )
		{
			status = 0;
			dataAvailable = false;
			dataLoss = false;
			byte[] readBuffer = new byte[ 2 ];

			if( !TryReadCommand( Module.FMRadioReception, command: 0x82, index: 0x01, readBuffer ) )
			{
				return false;
			}

			status = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
			dataAvailable = ( status & 0x8000 ) != 0;
			dataLoss = ( status & 0x4000 ) != 0;
			return true;
		}

		private bool TryGetFmRdsData( out RdsFrame frame )
		{
			frame = null;
			byte[] readBuffer = new byte[ 12 ];

			if( !TryReadCommand( Module.FMRadioReception, command: 0x83, index: 0x01, readBuffer ) )
			{
				return false;
			}

			ushort status = (ushort)( ( readBuffer[ 0 ] << 8 ) | readBuffer[ 1 ] );
			frame = new RdsFrame(
				status: status,
				blockA: (ushort)( ( readBuffer[ 2 ] << 8 ) | readBuffer[ 3 ] ),
				blockB: (ushort)( ( readBuffer[ 4 ] << 8 ) | readBuffer[ 5 ] ),
				blockC: (ushort)( ( readBuffer[ 6 ] << 8 ) | readBuffer[ 7 ] ),
				blockD: (ushort)( ( readBuffer[ 8 ] << 8 ) | readBuffer[ 9 ] ),
				decodeError: (ushort)( ( readBuffer[ 10 ] << 8 ) | readBuffer[ 11 ] ),
				timestampUtc: DateTime.UtcNow );
			return true;
		}

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

		public int PendingRdsFrameCount
		{
			get
			{
				lock( rdsSync )
				{
					return rdsFrameCount;
				}
			}
		}

		public List<string> RdsTextEntries
		{
			get
			{
				lock( rdsSync )
				{
					return new List<string>( rdsTextEntries );
				}
			}
		}

		public int PollRdsBuffer( int maxFrames )
		{
			if( maxFrames <= 0 )
			{
				throw new ArgumentOutOfRangeException( nameof( maxFrames ) );
			}

			if( !TryBeginRdsRead() )
			{
				return 0;
			}

			try
			{
				return CaptureRdsFrames( maxFrames, stopWhenPinReleased: false );
			}
			finally
			{
				EndRdsRead();
			}
		}

		public bool TryDequeueRdsFrame( out RdsFrame frame )
		{
			lock( rdsSync )
			{
				if( rdsFrameCount == 0 )
				{
					frame = null;
					return false;
				}

				frame = rdsFrames[ rdsReadIndex ];
				rdsFrames[ rdsReadIndex ] = null;
				rdsReadIndex = ( rdsReadIndex + 1 ) % rdsBufferCapacity;
				rdsFrameCount--;
				return true;
			}
		}

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

		private void InitializeRdsCapture()
		{
			if( tefGpioIndex < 0 )
			{
				throw new ArgumentOutOfRangeException( nameof( tefGpioIndex ), "TEF6686 GPIO index must be zero or greater." );
			}

			WriteSetCommand(
				Module.SystemAndApplicationControl,
				command: 0x03,
				index: 0x01,
				(ushort)tefGpioIndex,
				Module.FMRadioReception,
				(ushort)tefGpioOutput );

			if( tefGpioOutput != TefGpioOutput.Rds && tefGpioOutput != TefGpioOutput.QsiOrRdsActiveLow )
			{
				return;
			}

			WriteSetCommand( Module.FMRadioReception, command: 0x51, index: 0x01, 0x01, 0x02, 0x02 );

			GpioPinValue initialLevel = rdsPin.Read();

			if( !isRdsHandlerAttached )
			{
				rdsPin.ValueChanged += OnRdsPinValueChanged;
				isRdsHandlerAttached = true;
			}

			StartRdsServiceThread();

			if( enableRdsWatchdog )
			{
				StartRdsWatchdog();
			}

			if( initialLevel == GpioPinValue.Low )
			{
				RequestRdsService();
			}
			else if( TryGetFmRdsStatus( out ushort statusAfterSetup, out bool availableAfterSetup, out bool lossAfterSetup ) )
			{
				if( availableAfterSetup || lossAfterSetup )
				{
					RequestRdsService();
				}
			}
		}

		private void OnRdsPinValueChanged( GpioPin sender, GpioPinValueChangedEventArgs e )
		{
			lock( rdsSync )
			{
				lastRdsEdgeUtc = DateTime.UtcNow;
			}

			RequestRdsService();
		}

		private void StartRdsServiceThread()
		{
			if( isRdsServiceThreadStarted )
			{
				return;
			}

			isRdsServiceThreadStarted = true;

			Thread serviceThread = new( RdsServiceLoop );
			serviceThread.Start();
		}

		private void RequestRdsService()
		{
			lock( rdsSync )
			{
				rdsServiceRequested = true;
			}
		}

		private bool TryConsumeRdsServiceRequest()
		{
			lock( rdsSync )
			{
				if( !rdsServiceRequested )
				{
					return false;
				}

				rdsServiceRequested = false;
				return true;
			}
		}

		private void RdsServiceLoop()
		{
			DateTime nextLowLevelKickUtc = DateTime.UtcNow;

			while( true )
			{
				Thread.Sleep( rdsServiceIntervalMilliseconds );

				bool request = TryConsumeRdsServiceRequest();
				if( !request && DateTime.UtcNow >= nextLowLevelKickUtc && rdsPin.Read() == GpioPinValue.Low )
				{
					request = true;
					nextLowLevelKickUtc = DateTime.UtcNow.AddMilliseconds( rdsLowLevelKickIntervalMilliseconds );
				}

				if( !request )
				{
					continue;
				}

				if( !TryBeginRdsRead() )
				{
					continue;
				}

				try
				{
					if( !TryGetFmRdsStatus( out ushort status, out bool available, out bool loss ) )
					{
						Debug.WriteLine( "TEF6686 RDS service: status read failed" );
						continue;
					}

					if( !available && !loss )
					{
						continue;
					}

					CaptureRdsFrames( maxFrames: rdsBurstReadLimit, stopWhenPinReleased: true );
				}
				finally
				{
					EndRdsRead();
				}

				if( rdsPin.Read() == GpioPinValue.Low )
				{
					RequestRdsService();
				}
			}
		}

		private void StartRdsWatchdog()
		{
			if( isRdsWatchdogStarted )
			{
				return;
			}

			isRdsWatchdogStarted = true;

			Thread watchdogThread = new( RdsWatchdogLoop );
			watchdogThread.Start();
		}

		private void RdsWatchdogLoop()
		{
			while( true )
			{
				Thread.Sleep( rdsWatchdogIntervalMilliseconds );

				DateTime now = DateTime.UtcNow;
				DateTime edgeTime;
				lock( rdsSync )
				{
					edgeTime = lastRdsEdgeUtc;
				}

				if( edgeTime != DateTime.MinValue && ( now - edgeTime ).TotalMilliseconds < rdsWatchdogEdgeGraceMilliseconds )
				{
					continue;
				}

				if( !TryBeginRdsRead() )
				{
					continue;
				}

				try
				{
					if( !TryGetFmRdsStatus( out ushort status, out bool available, out bool loss ) )
					{
						continue;
					}

					if( !available && !loss )
					{
						continue;
					}

					CaptureRdsFrames( maxFrames: rdsBurstReadLimit, stopWhenPinReleased: false );
				}
				finally
				{
					EndRdsRead();
				}
			}
		}

		private bool TryBeginRdsRead()
		{
			lock( rdsSync )
			{
				if( isReadingRds )
				{
					return false;
				}

				isReadingRds = true;
				return true;
			}
		}

		private void EndRdsRead()
		{
			lock( rdsSync )
			{
				isReadingRds = false;
			}
		}

		private int CaptureRdsFrames( int maxFrames, bool stopWhenPinReleased )
		{
			int capturedFrames = 0;
			DateTime burstDeadline = DateTime.UtcNow.AddMilliseconds( rdsBurstWindowMilliseconds );

			for( int i = 0; i < maxFrames && DateTime.UtcNow < burstDeadline; )
			{
				if( !TryGetFmRdsData( out RdsFrame frame ) )
				{
					Debug.WriteLine( "TEF6686 RDS: Get_RDS_Data read failed" );
					break;
				}

				if( frame.HasData || frame.HasDataLoss )
				{
					EnqueueRdsFrame( frame );
					capturedFrames++;
					i++;
					if( frame.HasDataLoss )
					{
						Debug.WriteLine( $"TEF6686 RDS: data loss reported status=0x{frame.Status:X4}" );
					}

					if( stopWhenPinReleased && rdsPin.Read() == GpioPinValue.High )
					{
						break;
					}

					Thread.Sleep( rdsInterReadPauseMilliseconds );
					continue;
				}

				if( TryGetFmRdsStatus( out ushort statusSnapshot, out bool availableSnapshot, out bool lossSnapshot ) )
				{
					if( !availableSnapshot && !lossSnapshot )
					{
						break;
					}
				}
				else
				{
					Debug.WriteLine( "TEF6686 RDS: Get_RDS_Status read failed" );
					break;
				}

				if( !stopWhenPinReleased )
				{
					break;
				}

				Thread.Sleep( 10 );
			}

			return capturedFrames;
		}

		private void EnqueueRdsFrame( RdsFrame frame )
		{
			lock( rdsSync )
			{
				rdsFrames[ rdsWriteIndex ] = frame;
				rdsWriteIndex = ( rdsWriteIndex + 1 ) % rdsBufferCapacity;

				if( rdsFrameCount == rdsBufferCapacity )
				{
					rdsReadIndex = ( rdsReadIndex + 1 ) % rdsBufferCapacity;
				}
				else
				{
					rdsFrameCount++;
				}

				UpdateDecodedRdsText( frame );
			}
		}

		private void UpdateDecodedRdsText( RdsFrame frame )
		{
			if( !frame.HasData || !IsReliableFrameForText( frame ) )
			{
				return;
			}

			rdsPiCode = frame.BlockA;
			ushort groupTypeCode = (ushort)( ( frame.BlockB >> 12 ) & 0x0F );
			bool isGroupB = ( frame.BlockB & 0x0800 ) != 0;
			rdsPty = (ushort)( ( frame.BlockB >> 5 ) & 0x1F );

			if( groupTypeCode == 0 )
			{
				int segmentAddress = frame.BlockB & 0x03;
				int baseIndex = segmentAddress * 2;
				if( baseIndex + 1 < rdsProgramService.Length )
				{
					rdsProgramService[ baseIndex ] = (char)( frame.BlockD >> 8 );
					rdsProgramService[ baseIndex + 1 ] = (char)( frame.BlockD & 0xFF );
					rdsProgramServiceValid[ baseIndex ] = true;
					rdsProgramServiceValid[ baseIndex + 1 ] = true;
				}
			}
			else if( groupTypeCode == 2 )
			{
				byte abFlag = (byte)( ( frame.BlockB >> 4 ) & 0x01 );
				if( rdsRadioTextAbFlag != 0xFF && rdsRadioTextAbFlag != abFlag )
				{
					for( int i = 0; i < rdsRadioText.Length; i++ )
					{
						rdsRadioText[ i ] = ' ';
						rdsRadioTextValid[ i ] = false;
					}
				}

				rdsRadioTextAbFlag = abFlag;

				int segmentAddress = frame.BlockB & 0x0F;
				if( isGroupB )
				{
					int baseIndex = segmentAddress * 2;
					if( baseIndex + 1 < rdsRadioText.Length )
					{
						rdsRadioText[ baseIndex ] = (char)( frame.BlockD >> 8 );
						rdsRadioText[ baseIndex + 1 ] = (char)( frame.BlockD & 0xFF );
						rdsRadioTextValid[ baseIndex ] = true;
						rdsRadioTextValid[ baseIndex + 1 ] = true;
					}
				}
				else
				{
					int baseIndex = segmentAddress * 4;
					if( baseIndex + 3 < rdsRadioText.Length )
					{
						rdsRadioText[ baseIndex ] = (char)( frame.BlockC >> 8 );
						rdsRadioText[ baseIndex + 1 ] = (char)( frame.BlockC & 0xFF );
						rdsRadioText[ baseIndex + 2 ] = (char)( frame.BlockD >> 8 );
						rdsRadioText[ baseIndex + 3 ] = (char)( frame.BlockD & 0xFF );
						rdsRadioTextValid[ baseIndex ] = true;
						rdsRadioTextValid[ baseIndex + 1 ] = true;
						rdsRadioTextValid[ baseIndex + 2 ] = true;
						rdsRadioTextValid[ baseIndex + 3 ] = true;
					}
				}
			}

			RebuildRdsTextEntries();
		}

		private void RebuildRdsTextEntries()
		{
			rdsTextEntries.Clear();
			rdsTextEntries.Add( $"PI: 0x{rdsPiCode:X4}" );
			rdsTextEntries.Add( $"PTY: {rdsPty}" );

			if( CountValid( rdsProgramServiceValid ) >= rdsRequiredPsChars )
			{
				rdsTextEntries.Add( "PS: " + BuildDisplayText( rdsProgramService, rdsProgramServiceValid ).Trim() );
			}

			if( CountValid( rdsRadioTextValid ) >= rdsRequiredRtChars )
			{
				rdsTextEntries.Add( "RT: " + BuildDisplayText( rdsRadioText, rdsRadioTextValid ).Trim() );
			}
		}

		private static bool IsReliableFrameForText( RdsFrame frame )
		{
			if( frame.HasDataLoss )
			{
				return false;
			}

			return frame.DecodeError == 0;
		}

		private static int CountValid( bool[] validFlags )
		{
			int count = 0;
			for( int i = 0; i < validFlags.Length; i++ )
			{
				if( validFlags[ i ] )
				{
					count++;
				}
			}

			return count;
		}

		private static string BuildDisplayText( char[] chars, bool[] validFlags )
		{
			char[] result = new char[ chars.Length ];
			for( int i = 0; i < chars.Length; i++ )
			{
				result[ i ] = validFlags[ i ] ? SanitizeRdsChar( chars[ i ] ) : ' ';
			}

			return new string( result );
		}

		private static char SanitizeRdsChar( char c )
		{
			return c >= 32 && c <= 126 ? c : ' ';
		}

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

		private void WriteBuffer( byte[] buffer )
		{
			I2cTransferResult result = i2CDevice.WritePartial( buffer );

			if( result.Status != I2cTransferStatus.FullTransfer )
			{
				throw new Exception( $"TEF6686 I2C write failed: {result.Status}" );
			}
		}

		private bool TryOpenDevice( string i2cControllerName, byte address )
		{
			I2cController i2cController = I2cController.FromName( i2cControllerName );

			try
			{
				this.i2CDevice = i2cController.GetDevice(
					new I2cConnectionSettings( address, I2cMode.Master, I2cAddressFormat.SevenBit, 100_000U ) );
			}
			catch( Exception ex )
			{
				Debug.WriteLine( $"TEF6686 I2C probe 0x{address:X2} failed: {ex.Message}" );
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
				Debug.WriteLine( $"I2C probe failed for address 0x{address:X2}: {ex.Message}" );
			}

			return false;
		}
	}
}
