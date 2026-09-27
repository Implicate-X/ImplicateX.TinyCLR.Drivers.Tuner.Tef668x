using System;
using System.Diagnostics;
using System.Threading;
using GHIElectronics.TinyCLR.Pins;

namespace ImplicateX.TinyCLR.Drivers.Tuner.Tef668x
{
	internal class Program
	{
		static Device device;

		static void Main()
		{
			device = new(
				i2cControllerName: FEZDuino.I2cBus.I2c1,
				resPinID: FEZDuino.GpioPin.PE1,
				rdsPinID: FEZDuino.GpioPin.PE0 );

			device.Initialize();
			device.SetAudioMute( mute: false );
			device.TuneToFm( frequency10kHz: 9120 );

			Thread.Sleep( 250 );

			if( device.TryGetFmQualityStatus( out ushort qualityStatus ) )
			{
				Debug.WriteLine( $"FM status tuned/raw: 0x{qualityStatus:X4}" );
			}

			if( device.TryGetFmRdsStatus( out ushort rdsStatus, out bool rdsAvailable, out bool rdsDataLoss ) )
			{
				Debug.WriteLine( $"FM status rds/raw: 0x{rdsStatus:X4} available={rdsAvailable} loss={rdsDataLoss}" );
				Debug.WriteLine( $"FM status rds: hasData={rdsAvailable} hasDataLoss={rdsDataLoss}" );
			}

			if( device.TryGetFmSignalStatus( out ushort signalStatus ) )
			{
				bool stereoAvailable = ( signalStatus & 0x8000 ) != 0;
				bool digitalAvailable = ( signalStatus & 0x4000 ) != 0;
				Debug.WriteLine( $"FM status signal/raw: 0x{signalStatus:X4}" );
				Debug.WriteLine( $"FM status signal: stereoAvailable={stereoAvailable} digitalAvailable={digitalAvailable}" );
			}

			if( device.TryGetFmProcessingStatus( out ushort softmute, out ushort highcut, out ushort stereo, out ushort sthiblend ) )
			{
				bool softmuteActive = softmute > 0;
				bool highcutActive = highcut > 0;
				bool stereoProcessingActive = stereo > 0;
				bool stHiBlendActive = sthiblend > 0;
				Debug.WriteLine( $"FM status processing: softmute={softmute} highcut={highcut} stereo={stereo} sthiblend={sthiblend}" );
				Debug.WriteLine( $"FM status processing/flags: softmuteActive={softmuteActive} highcutActive={highcutActive} stereoProcessingActive={stereoProcessingActive} stHiBlendActive={stHiBlendActive}" );
			}

			while( true )
			{
				Thread.Sleep( 1000 );
			}
		}
	}
}
