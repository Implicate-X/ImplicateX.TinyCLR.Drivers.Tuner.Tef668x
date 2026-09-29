using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GHIElectronics.TinyCLR.Pins;

namespace ImplicateX.TinyCLR.Drivers.Tuner.Tef668x
{
	internal class Program
	{
		static Device device;
		const int tefRdsGpioIndex = 0;
		const TefGpioOutput tefGpioOutput = TefGpioOutput.Rds;

		static void Main()
		{
			device = new(
				i2cControllerName: FEZDuino.I2cBus.I2c1,
				resPinID: FEZDuino.GpioPin.PE1,
				rdsPinID: FEZDuino.GpioPin.PE0,
				tefGpioIndex: tefRdsGpioIndex,
				tefGpioOutput: tefGpioOutput );

			device.Initialize();
			device.SetAudioMute( mute: false );
			device.TuneToFm( frequency10kHz: 10130 );

			Debug.WriteLine( $"TEF GPIO test mode: {tefGpioOutput}" );

			//Thread statusThread = new( StatusLoop );
			//statusThread.Start();

			if( tefGpioOutput == TefGpioOutput.Rds || tefGpioOutput == TefGpioOutput.QsiOrRdsActiveLow )
			{
				Thread displayThread = new( DisplayLoop );
				displayThread.Start();
			}

			while( true )
			{
				Thread.Sleep( 1000 );
			}
		}

		static void StatusLoop()
		{
			ushort lastQuality = 0xFFFF;
			ushort lastRds = 0xFFFF;
			ushort lastSignal = 0xFFFF;
			ushort lastSoftmute = 0xFFFF;
			ushort lastHighcut = 0xFFFF;
			ushort lastStereo = 0xFFFF;
			ushort lastStHiBlend = 0xFFFF;

			while( true )
			{
				if( device.TryGetFmQualityStatus( out ushort qualityStatus ) && qualityStatus != lastQuality )
				{
					lastQuality = qualityStatus;
					Debug.WriteLine( $"FM quality/raw: 0x{qualityStatus:X4}" );
				}

				if( device.TryGetFmRdsStatus( out ushort rdsStatus, out bool rdsAvailable, out bool rdsDataLoss ) && rdsStatus != lastRds )
				{
					lastRds = rdsStatus;
					Debug.WriteLine( $"FM RDS/QSI raw: 0x{rdsStatus:X4} available={rdsAvailable} loss={rdsDataLoss}" );
				}

				if( device.TryGetFmSignalStatus( out ushort signalStatus ) && signalStatus != lastSignal )
				{
					lastSignal = signalStatus;
					bool stereoAvailable = ( signalStatus & 0x8000 ) != 0;
					bool digitalAvailable = ( signalStatus & 0x4000 ) != 0;
					Debug.WriteLine( $"FM signal/raw: 0x{signalStatus:X4} stereoAvailable={stereoAvailable} digitalAvailable={digitalAvailable}" );
				}

				if( device.TryGetFmProcessingStatus( out ushort softmute, out ushort highcut, out ushort stereo, out ushort sthiblend ) )
				{
					if( softmute != lastSoftmute || highcut != lastHighcut || stereo != lastStereo || sthiblend != lastStHiBlend )
					{
						lastSoftmute = softmute;
						lastHighcut = highcut;
						lastStereo = stereo;
						lastStHiBlend = sthiblend;
						Debug.WriteLine( $"FM processing: softmute={softmute} highcut={highcut} stereo={stereo} sthiblend={sthiblend}" );
					}
				}

				Thread.Sleep( 250 );
			}
		}

		static string BuildRdsTextLine( List<string> entries )
		{
			string line = string.Empty;
			for( int i = 0; i < entries.Count; i++ )
			{
				if( i > 0 )
				{
					line += " | ";
				}

				line += entries[ i ];
			}

			return line;
		}

		static void DisplayLoop()
		{
			while( true )
			{
				while( device.TryDequeueRdsFrame( out Device.RdsFrame frame ) )
				{
					Debug.WriteLine( $"RDS buffer[{device.PendingRdsFrameCount}]: A=0x{frame.BlockA:X4} B=0x{frame.BlockB:X4} C=0x{frame.BlockC:X4} D=0x{frame.BlockD:X4} err=0x{frame.DecodeError:X4} status=0x{frame.Status:X4}" );

					var rdsTextEntries = device.RdsTextEntries;
					Debug.WriteLine( rdsTextEntries.Count > 0
						? "RDS text: " + BuildRdsTextLine( rdsTextEntries )
						: "RDS text: <none>" );
				}

				Thread.Sleep( 100 );
			}
		}
	}
}
