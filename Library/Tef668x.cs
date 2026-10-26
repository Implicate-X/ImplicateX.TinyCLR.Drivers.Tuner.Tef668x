using System;
using System.Diagnostics;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.I2c;

namespace ImplicateX.TinyCLR.Drivers.Tuner
{
	public partial class Tef668x
	(
		string i2cControllerName,
		GpioPin resPin,
		GpioPin rdsPin,
		int tefGpioIndex = 0,
		bool enableRdsWatchdog = false
	)
	{
		private const byte i2cAddress = 0x64;
		private const int patchChunkBytes = 24;
		private const int rdsBufferCapacity = 32;
		private const int rdsBurstReadLimit = 22;
		private const int rdsBurstWindowMilliseconds = 1920;
		private const int rdsServiceIntervalMilliseconds = 10;
		private const int rdsLowLevelResyncIntervalMilliseconds = 87;
		private const int rdsWatchdogIntervalMilliseconds = 75;
		private const int rdsWatchdogEdgeGraceMilliseconds = 300;
		private I2cDevice i2CDevice;
		private PatchEngine patchEngine;
		private readonly object rdsSync = new();
		private readonly RdsFrame[] rdsFrames = new RdsFrame[ rdsBufferCapacity ];
		private int rdsWriteIndex;
		private int rdsReadIndex;
		private int rdsFrameCount;
		private bool isReadingRds;
		private bool isRdsHandlerAttached;
		private bool isRdsServiceThreadStarted;
		private bool rdsServiceRequested;
		private bool isRdsWatchdogStarted;
		private DateTime lastRdsEdgeUtc = DateTime.MinValue;

		public sealed class RdsFrame( ushort status, ushort blockA, ushort blockB, ushort blockC, ushort blockD, ushort decodeError, DateTime timestampUtc )
		{
			public ushort Status { get; } = status;
			public ushort BlockA { get; } = blockA;
			public ushort BlockB { get; } = blockB;
			public ushort BlockC { get; } = blockC;
			public ushort BlockD { get; } = blockD;
			public ushort DecodeError { get; } = decodeError;
			public DateTime TimestampUtc { get; } = timestampUtc;

			public bool HasData => ( Status & 0x8000 ) != 0;
			public bool HasDataLoss => ( Status & 0x4000 ) != 0;
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
			Debug.WriteLine( "TEF6686 init: open GPIO pins" );

			Debug.WriteLine( "TEF6686 init: hardware reset pulse" );
			resPin.Write( GpioPinValue.Low );
			Thread.Sleep( 5 );
			resPin.Write( GpioPinValue.High );
			Thread.Sleep( 5 );

			Debug.WriteLine( $"TEF6686 init: probe I2C address 0x{i2cAddress:X2}" );
			if( !TryOpenDevice( i2cControllerName: i2cControllerName, address: i2cAddress ) )
			{
				throw new Exception( $"TEF6686 I2C device not found at address 0x{i2cAddress:X2} on controller {i2cControllerName}" );
			}

			if( !TryGetOperationStatus( out ushort currentStatus ) )
			{
				throw new Exception( "TEF6686 operation status read failed." );
			}

			Debug.WriteLine( $"TEF6686 init: detected state 0x{currentStatus:X4}" );

			if( currentStatus == (ushort)OperationStatus.Boot )
			{
				Debug.WriteLine( "TEF6686 init: boot state detected, running startup sequence" );
				Debug.WriteLine( "TEF6686 init: apply patch/LUT" );
				patchEngine = new( this );
				patchEngine.ApplyPatch();

				Debug.WriteLine( "TEF6686 init: send Start" );
				WriteRawCommand( command: 0x14, 0x0001 );

				Debug.WriteLine( "TEF6686 init: wait for idle state" );
				if( !WaitForOperationStatus( OperationStatus.Idle, timeoutMilliseconds: 1500 ) )
				{
					throw new Exception( "TEF6686 did not enter idle state after Start command." );
				}

				Debug.WriteLine( "TEF6686 init: send Activate mode=1" );
				WriteSetCommand( Module.SystemAndApplicationControl, command: 0x05, index: 0x01, 0x0001 );

				Debug.WriteLine( "TEF6686 init: wait for active standby state" );
				if( !WaitForOperationStatus( OperationStatus.ActiveStandby, timeoutMilliseconds: 2000 ) )
				{
					throw new Exception( "TEF6686 did not enter active standby state after Activate command." );
				}
			}
			else if( currentStatus == (ushort)OperationStatus.Idle )
			{
				Debug.WriteLine( "TEF6686 init: idle state detected, skipping patch/start" );
				Debug.WriteLine( "TEF6686 init: send Activate mode=1" );
				WriteSetCommand( Module.SystemAndApplicationControl, command: 0x05, index: 0x01, 0x0001 );

				Debug.WriteLine( "TEF6686 init: wait for active state" );
				if( !WaitForOperationStatus( OperationStatus.ActiveStandby, timeoutMilliseconds: 2000 ) )
				{
					throw new Exception( "TEF6686 did not enter active state after Activate command." );
				}
			}
			else if( IsActiveState( currentStatus ) )
			{
				Debug.WriteLine( "TEF6686 init: active state already present, skipping startup sequence" );
			}
			else
			{
				throw new Exception( $"TEF6686 unsupported operation status 0x{currentStatus:X4}" );
			}

			Debug.WriteLine( "TEF6686 init: read identification" );
			if( TryGetIdentification( out ushort deviceId, out ushort hwVersion, out ushort swVersion ) )
			{
				Debug.WriteLine( $"TEF6686 identification: device=0x{deviceId:X4}, hw=0x{hwVersion:X4}, sw=0x{swVersion:X4}" );
			}
			else
			{
				Debug.WriteLine( "TEF6686 identification read failed" );
			}

			InitializeRdsCapture();

			Debug.WriteLine( "TEF6686 init: startup sequence complete" );
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
						Debug.WriteLine( $"TEF6686 status reached: {expectedStatus} (0x{status:X4})" );
						return true;
					}
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
			Debug.WriteLine( $"TEF6686 audio mute: {( mute ? "on" : "off" )}" );
		}

		public void TuneToFm( ushort frequency10kHz )
		{
			if( frequency10kHz < 6500 || frequency10kHz > 10800 )
			{
				throw new ArgumentOutOfRangeException( nameof( frequency10kHz ), "FM frequency must be in 10 kHz units between 6500 and 10800." );
			}

			WriteSetCommand( Module.FMRadioReception, command: 0x01, index: 0x01, 0x0001, frequency10kHz );
			Debug.WriteLine( $"TEF6686 FM tune: {frequency10kHz / 100}.{frequency10kHz % 100:D2} MHz" );
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

		//public int PollRdsBuffer( int maxFrames )
		//{
		//	if( maxFrames <= 0 )
		//	{
		//		throw new ArgumentOutOfRangeException( nameof( maxFrames ) );
		//	}

		//	if( !TryBeginRdsRead() )
		//	{
		//		return 0;
		//	}

		//	try
		//	{
		//		return CaptureRdsFrames( maxFrames, stopWhenPinReleased: false );
		//	}
		//	finally
		//	{
		//		EndRdsRead();
		//	}
		//}

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

			Debug.WriteLine( $"TEF6686 RDS: route DAVN to tuner GPIO {tefGpioIndex}" );
			WriteSetCommand( Module.SystemAndApplicationControl, command: 0x03, index: 0x01, (ushort)tefGpioIndex, Module.FMRadioReception, 0x0101 );
			Debug.WriteLine( "TEF6686 RDS: enable data-available signal pin" );
			WriteSetCommand( Module.FMRadioReception, command: 0x51, index: 0x01, 0x01, 0x02, 0x02 );

			GpioPinValue initialLevel = rdsPin.Read();
			Debug.WriteLine( $"TEF6686 RDS: pin level after setup = {initialLevel}" );

			if( !isRdsHandlerAttached )
			{
				Debug.WriteLine( "TEF6686 RDS: attach GPIO interrupt handler" );
				rdsPin.ValueChanged += OnRdsPinValueChanged;
				isRdsHandlerAttached = true;
			}

			StartRdsServiceThread();

			if( enableRdsWatchdog )
			{
				StartRdsWatchdog();
			}
			else
			{
				Debug.WriteLine( "TEF6686 RDS: watchdog disabled (IRQ-only mode)" );
			}

			if( TryGetFmRdsStatus( out ushort statusAfterSetup, out bool availableAfterSetup, out bool lossAfterSetup ) )
			{
				Debug.WriteLine( $"TEF6686 RDS: status after setup=0x{statusAfterSetup:X4} available={availableAfterSetup} loss={lossAfterSetup}" );
				if( availableAfterSetup || lossAfterSetup )
				{
					Debug.WriteLine( "TEF6686 RDS: data already pending after init, schedule service" );
					RequestRdsService();
				}
			}
			else
			{
				Debug.WriteLine( "TEF6686 RDS: status read after setup failed" );
			}
		}

		private void OnRdsPinValueChanged( GpioPin sender, GpioPinValueChangedEventArgs e )
		{
			lock( rdsSync )
			{
				lastRdsEdgeUtc = DateTime.UtcNow;
			}

			GpioPinValue level = sender.Read();
			Debug.WriteLine( $"TEF6686 RDS: edge={e.Edge} level={level}" );
			RequestRdsService();
		}

		private void StartRdsServiceThread()
		{
			if( isRdsServiceThreadStarted )
			{
				return;
			}

			isRdsServiceThreadStarted = true;
			Debug.WriteLine( "TEF6686 RDS: start IRQ service worker" );

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
			DateTime nextLowLevelResyncUtc = DateTime.MinValue;

			while( true )
			{
				Thread.Sleep( rdsServiceIntervalMilliseconds );

				bool request = TryConsumeRdsServiceRequest();
				bool lowLevelResync = false;

				if( !request && DateTime.UtcNow >= nextLowLevelResyncUtc && rdsPin.Read() == GpioPinValue.Low )
				{
					lowLevelResync = true;
					nextLowLevelResyncUtc = DateTime.UtcNow.AddMilliseconds( rdsLowLevelResyncIntervalMilliseconds );
				}

				if( !request && !lowLevelResync )
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

					Debug.WriteLine( $"TEF6686 RDS service: status=0x{status:X4} available={available} loss={loss}" );
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
			}
		}

		private void StartRdsWatchdog()
		{
			if( isRdsWatchdogStarted )
			{
				return;
			}

			isRdsWatchdogStarted = true;
			Debug.WriteLine( "TEF6686 RDS: start watchdog" );

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

					Debug.WriteLine( $"TEF6686 RDS watchdog: pending status=0x{status:X4} available={available} loss={loss}" );
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
			Debug.WriteLine( $"TEF6686 RDS: capture start max={maxFrames} stopOnPinRelease={stopWhenPinReleased} pin={rdsPin.Read()}" );

			for( int i = 0; i < maxFrames && DateTime.UtcNow < burstDeadline; )
			{
				if( !TryGetFmRdsData( out RdsFrame frame ) )
				{
					Debug.WriteLine( "TEF6686 RDS: Get_RDS_Data read failed, stop capture" );
					break;
				}

				if( frame.HasData || frame.HasDataLoss )
				{
					EnqueueRdsFrame( frame );
					capturedFrames++;
					i++;
					Debug.WriteLine( $"TEF6686 RDS frame: A=0x{frame.BlockA:X4} B=0x{frame.BlockB:X4} C=0x{frame.BlockC:X4} D=0x{frame.BlockD:X4} err=0x{frame.DecodeError:X4} status=0x{frame.Status:X4}" );
					continue;
				}

				if( TryGetFmRdsStatus( out ushort statusSnapshot, out bool availableSnapshot, out bool lossSnapshot ) )
				{
					Debug.WriteLine( $"TEF6686 RDS: empty data read status=0x{statusSnapshot:X4} available={availableSnapshot} loss={lossSnapshot} pin={rdsPin.Read()}" );
					if( !availableSnapshot && !lossSnapshot )
					{
						Debug.WriteLine( "TEF6686 RDS: no pending RDS data after empty read, stop capture" );
						break;
					}
				}
				else
				{
					Debug.WriteLine( "TEF6686 RDS: empty data read and Get_RDS_Status failed" );
					break;
				}

				if( !stopWhenPinReleased )
				{
					Debug.WriteLine( "TEF6686 RDS: stop capture after empty read (polling mode)" );
					break;
				}

				Thread.Sleep( 10 );
			}

			if( DateTime.UtcNow >= burstDeadline )
			{
				Debug.WriteLine( "TEF6686 RDS: capture deadline reached" );
			}

			Debug.WriteLine( $"TEF6686 RDS: capture end frames={capturedFrames} pin={rdsPin.Read()}" );
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
			}
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

				Debug.WriteLine( $"TEF6686 I2C probe 0x{address:X2} succeeded on controller {i2cControllerName}" );
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
