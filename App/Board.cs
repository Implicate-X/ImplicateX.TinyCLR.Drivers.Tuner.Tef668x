using System;
using System.Diagnostics;
using System.Threading;
using GHIElectronics.TinyCLR.Pins;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.I2c;
using ImplicateX.TinyCLR.Drivers.Expander;
using ImplicateX.TinyCLR.Drivers.Tuner;

namespace ImplicateX.TinyCLR.Drivers.Tuner
{
	public class Board
	{
		public static class ControllerPin
		{
			public const int RemoteCtrl = FEZFeather.GpioPin.PA0;
			public const int EthernetCS = FEZFeather.GpioPin.PA3;
			public const int EthernetMISO = FEZFeather.GpioPin.PA6;
			public const int EthernetMOSI = FEZFeather.GpioPin.PA7;
			public const int EthernetSCLK = FEZFeather.GpioPin.PA5;
			public const int PowerSwitch = FEZFeather.GpioPin.PB1;
			public const int CodecSCLK = FEZFeather.GpioPin.PE12;
			public const int CodecMOSI = FEZFeather.GpioPin.PE14;
			public const int CodecMISO = FEZFeather.GpioPin.PE13;
			public const int EthernetINT = FEZFeather.GpioPin.PA10;
			public const int EthernetReset = FEZFeather.GpioPin.PA9;
			public const int DecoderSCLK = FEZFeather.GpioPin.PC8;
			public const int DecoderMOSI = FEZFeather.GpioPin.PC9;
			public const int DecoderMISO = FEZFeather.GpioPin.PC10;
			public const int DecoderCS = FEZFeather.GpioPin.PC11;
			public const int CodecGPIO1 = FEZFeather.GpioPin.PD2;

			public const int TunerRDS = FEZFeather.GpioPin.PE6;
			public const int CodecCS = FEZFeather.GpioPin.PE0;
			public const int CodecDCS = FEZFeather.GpioPin.PE1;
			public const int VFReset = FEZFeather.GpioPin.PD6;
			public const int VFDCS = FEZFeather.GpioPin.PD5;
			public const int VFDData = FEZFeather.GpioPin.PD4;
			public const int VFDClock = FEZFeather.GpioPin.PD3;
			public const int A1SCL = FEZFeather.GpioPin.PB8;
			public const int A1SDA = FEZFeather.GpioPin.PB9;
			public const int V1INTA = FEZFeather.GpioPin.PD0;
			public const int V1Reset = FEZFeather.GpioPin.PD1;
			public const int CodecDREQ = FEZFeather.GpioPin.PE7;
			public const int CodecReset = FEZFeather.GpioPin.PE8;
		}

		public static class ExpanderPin
		{
			public const int KeyMenu = 0;
			public const int KeyAutopreset = 1;
			public const int KeyBand = 2;
			public const int KeyDimmer = 3;
			public const int EncoderA = 5;
			public const int EncoderB = 6;
			public const int EncoderKey = 7;
			public const int V2OutputEnable = 8;
			public const int V3OutputEnable = 9;
			public const int AudioSwitch = 10;
			public const int TunerReset = 11;
			public const int RTCEVI = 12;
			public const int LedRDS = 13;
			public const int LedTuned = 14;
			public const int LedStereo = 15;
		}

		private GpioController gpioController;
		private I2cController i2cController;
		private readonly Mcp23017 mcp23017;
		private readonly Tef668x tef668x;
		private GpioPin tunerResPin;
		private GpioPin tunerRdsPin;

		public Board()
		{
			Initialize();

			tef668x = new(
				i2cControllerName: FEZDuino.I2cBus.I2c1,
				resPin: tunerResPin,
				rdsPin: tunerRdsPin );

			tef668x.Initialize();
			tef668x.SetAudioMute( mute: false );
			tef668x.TuneToFm( frequency10kHz: 9120 );

		}

		private void Initialize()
		{
			gpioController = GpioController.GetDefault();
			i2cController = I2cController.FromName( FEZDuino.I2cBus.I2c1 );

			GpioPin v1ResetPin = gpioController.OpenPin( ControllerPin.V1Reset );
			v1ResetPin.SetDriveMode( GpioPinDriveMode.Output );
			v1ResetPin.Write( GpioPinValue.High );
			Thread.Sleep( 100 );
			v1ResetPin.Write( GpioPinValue.Low );
			Thread.Sleep( 100 );
			v1ResetPin.Write( GpioPinValue.High );
			Thread.Sleep( 100 );

			var mcpDevice = CreateI2cDevice(
				i2cController,
				new I2cConnectionSettings( 0x20, I2cMode.Master, I2cAddressFormat.SevenBit, 100_000U ) );

			var intPin = gpioController.OpenPin( ControllerPin.V1INTA );
			intPin.SetDriveMode( GpioPinDriveMode.InputPullUp );

			var mcp23017 = new Mcp23017( mcpDevice, interruptPin: intPin );

			GpioPin v2OutputEnable = mcp23017.OpenPin( ExpanderPin.V2OutputEnable );
			v2OutputEnable.SetDriveMode( GpioPinDriveMode.Output );
			v2OutputEnable.Write( GpioPinValue.Low );

			GpioPin v3OutputEnable = mcp23017.OpenPin( ExpanderPin.V3OutputEnable );
			v3OutputEnable.SetDriveMode( GpioPinDriveMode.Output );
			v3OutputEnable.Write( GpioPinValue.Low );

			GpioPin audioSwitchPin = mcp23017.OpenPin( ExpanderPin.AudioSwitch );
			audioSwitchPin.SetDriveMode( GpioPinDriveMode.Output );
			audioSwitchPin.Write( GpioPinValue.Low );

			tunerResPin = mcp23017.OpenPin( ExpanderPin.TunerReset );
			tunerRdsPin = gpioController.OpenPin( ControllerPin.TunerRDS );

			tunerResPin.SetDriveMode( GpioPinDriveMode.Output );
			tunerRdsPin.SetDriveMode( GpioPinDriveMode.InputPullUp );

		}

		private static I2cDevice CreateI2cDevice( I2cController controller, I2cConnectionSettings settings )
		{
			var type = controller.GetType();
			var method =
				type.GetMethod( "CreateI2cDevice", new[] { typeof( I2cConnectionSettings ) } ) ??
				type.GetMethod( "GetDevice", new[] { typeof( I2cConnectionSettings ) } );

			if( method == null )
			{
				throw new NotSupportedException( "Keine kompatible Methode zum Erzeugen eines I2C-Geräts gefunden." );
			}

			return ( I2cDevice )method.Invoke( controller, new object[] { settings } );
		}

		private bool ProbeAddress( int address )
		{
			bool result = false;

			if( i2cController == null )
			{
				Debug.WriteLine( "I2C controller not initialized!" );
				return false;
			}

			I2cDevice dev =
				i2cController.GetDevice(
					new I2cConnectionSettings( address, I2cMode.Master, I2cAddressFormat.SevenBit, 100_000U ) );

			byte[] rBuf = new byte[ 1 ];

			try
			{
				if( dev.ReadPartial( rBuf ).Status != I2cTransferStatus.SlaveAddressNotAcknowledged )
				{
					result = true;
				}
			}
			catch( Exception ex )
			{
				Debug.WriteLine( $"I2C probe failed for address 0x{address:X2}: {ex.Message}" );
				result = false;
			}

			dev.Dispose();

			return result;
		}
	}
}
