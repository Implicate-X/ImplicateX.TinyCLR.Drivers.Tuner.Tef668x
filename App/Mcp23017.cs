using System;
using System.Reflection;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;
using GHIElectronics.TinyCLR.Devices.Gpio.Provider;
using GHIElectronics.TinyCLR.Devices.I2c;
using GHIElectronics.TinyCLR.Devices.Spi;

namespace ImplicateX.TinyCLR.Drivers.Expander
{
	public sealed partial class Mcp23017 : IDisposable
	{
		private const int PinCountValue = 16;

		private const byte IoConIntPolMask = 0x02;
		private const byte IoConOdrMask = 0x04;
		private const byte IoConMirrorMask = 0x40;

		public enum Port : byte
		{
			A = 0,
			B = 1
		}

		public enum PinMode : byte
		{
			Input = 0,
			Output = 1,
			InputPullUp = 2
		}

		public enum PinLevel : byte
		{
			Low = 0,
			High = 1
		}

		private readonly I2cDevice i2cDevice;
		private readonly SpiDevice spiDevice;
		private readonly GpioPin resetPin;
		private readonly bool useSpi;
		private readonly byte i2cAddress;
		private readonly byte spiAddress;

		private MethodInfo cachedI2cWriteMethod;
		private MethodInfo cachedI2cReadMethod;
		private MethodInfo cachedI2cWriteReadMethod;

		private readonly GpioControllerProvider provider;
		private readonly GpioController controller;
		private bool disposed;

		public Mcp23017( I2cDevice device, byte address = 0x20, GpioPin resetPin = null, GpioPin interruptPin = null )
		{
			this.i2cDevice = device ?? throw new ArgumentNullException( nameof( device ) );
			this.i2cAddress = address;
			this.resetPin = resetPin;
			this.useSpi = false;

			this.provider = new GpioControllerProvider( this, interruptPin );
			this.controller = GpioController.FromProvider( this.provider );

			this.SoftReset();
		}

		public Mcp23017( SpiDevice device, byte spiAddress, GpioPin resetPin = null, GpioPin interruptPin = null )
		{
			this.spiDevice = device ?? throw new ArgumentNullException( nameof( device ) );
			this.spiAddress = spiAddress;
			this.resetPin = resetPin;
			this.useSpi = true;

			this.provider = new GpioControllerProvider( this, interruptPin );
			this.controller = GpioController.FromProvider( this.provider );

			this.SoftReset();
		}

		public GpioController Controller
		{
			get { return this.controller; }
		}

		public int PinCount
		{
			get { return PinCountValue; }
		}

		public bool Init()
		{
			this.SoftReset();
			return true;
		}

		public void Reset()
		{
			if( this.resetPin != null )
			{
				this.resetPin.Write( GpioPinValue.Low );
				Thread.Sleep( 10 );
				this.resetPin.Write( GpioPinValue.High );
				Thread.Sleep( 10 );
			}

			this.SoftReset();
		}

		public GpioPin OpenPin( int pinNumber )
		{
			return this.controller.OpenPin( pinNumber );
		}

		public GpioPin[] OpenPins( params int[] pinNumbers )
		{
			return this.controller.OpenPins( pinNumbers );
		}

		public bool TryOpenPin( int pinNumber, out GpioPin pin )
		{
			return this.controller.TryOpenPin( pinNumber, out pin );
		}

		public bool TryOpenPins( out GpioPin[] pins, params int[] pinNumbers )
		{
			return this.controller.TryOpenPins( out pins, pinNumbers );
		}

		public void SetPinMode( byte pin, Port port, PinMode pinState )
		{
			this.provider.SetDriveMode( ToPinIndex( port, pin ), this.ToDriveMode( pinState ) );
		}

		public void SetPortMode( byte portState, Port port )
		{
			this.provider.SetPortMode( portState, port );
		}

		public byte GetPortMode( Port port )
		{
			return unchecked( (byte)~this.ReadRegister( Register.IODIR, port ) );
		}

		public void SetPortMode( byte portState, Port port, PinMode pu )
		{
			this.provider.SetPortMode( portState, port, pu );
		}

		public void SetPin( byte pin, Port port, PinLevel pinLevel )
		{
			byte olat = this.GetOutputLatch( port );
			byte mask = (byte)( 1 << pin );

			if( pinLevel == PinLevel.High )
			{
				olat |= mask;
			}
			else
			{
				olat &= (byte)~mask;
			}

			this.SetOutputLatch( olat, port );
		}

		public void TogglePin( byte pin, Port port )
		{
			byte olat = this.GetOutputLatch( port );
			byte mask = (byte)( 1 << pin );

			if( ( olat & mask ) == 0 )
			{
				olat |= mask;
			}
			else
			{
				olat &= (byte)~mask;
			}

			this.SetOutputLatch( olat, port );
		}

		public void SetPinX( byte pin, Port port, PinMode pinState, PinLevel pinLevel )
		{
			this.SetPinMode( pin, port, pinState );
			this.SetPin( pin, port, pinLevel );
		}

		public void SetAllPins( Port port, PinLevel pinLevel )
		{
			this.SetOutputLatch( pinLevel == PinLevel.High ? (byte)0xFF : (byte)0x00, port );
		}

		public void SetPort( byte olat, Port port )
		{
			this.SetOutputLatch( olat, port );
		}

		public byte GetPort( Port port )
		{
			return this.ReadRegister( Register.GPIO, port );
		}

		public byte GetOutputLatch( Port port )
		{
			return this.ReadRegister( Register.OLAT, port );
		}

		public void SetPort( byte portLevelA, byte portLevelB )
		{
			this.SetOutputLatch( portLevelA, Port.A );
			this.SetOutputLatch( portLevelB, Port.B );
		}

		public void SetPortX( byte portState, byte portLevel, Port port )
		{
			this.SetPortMode( portState, port );
			this.SetOutputLatch( portLevel, port );
		}

		public void SetPinPullUp( byte pin, Port port, PinLevel pinLevel )
		{
			byte gppu = this.GetPortPullUp( port );
			byte mask = (byte)( 1 << pin );

			if( pinLevel == PinLevel.High )
			{
				gppu |= mask;
			}
			else
			{
				gppu &= (byte)~mask;
			}

			this.SetPortPullUp( gppu, port );
		}

		public void SetPortPullUp( byte pulledUpPins, Port port )
		{
			this.WriteRegister( Register.GPPU, port, pulledUpPins );
		}

		public byte GetPortPullUp( Port port )
		{
			return this.ReadRegister( Register.GPPU, port );
		}

		public bool GetPin( byte pin, Port port )
		{
			byte result = this.ReadRegister( Register.GPIO, port );
			result &= (byte)( 1 << pin );
			return result != 0;
		}

		public void SetInterruptPinPol( byte level )
		{
			this.provider.SetInterruptPinPol( level );
		}

		public void SetIntOdr( byte openDrain )
		{
			this.provider.SetIntOdr( openDrain );
		}

		public void SetIntMirror( byte mirrored )
		{
			this.provider.SetIntMirror( mirrored );
		}

		public void SetInterruptOnChangePin( byte pin, Port port )
		{
			this.provider.SetInterruptOnChangePin( pin, port );
		}

		public void SetInterruptOnDefValDevPin( byte pin, Port port, byte pinIntLevel )
		{
			this.provider.SetInterruptOnDefValDevPin( pin, port, pinIntLevel );
		}

		public void SetInterruptOnChangePort( byte intOnChangePins, Port port )
		{
			this.provider.SetInterruptOnChangePort( intOnChangePins, port );
		}

		public void SetInterruptOnDefValDevPort( byte intPins, Port port, byte defVal )
		{
			this.provider.SetInterruptOnDefValDevPort( intPins, port, defVal );
		}

		public void DeleteAllInterruptsOnPort( Port port )
		{
			this.provider.DeleteAllInterruptsOnPort( port );
		}

		public void SetIpolPin( byte pin, Port port, byte ipolState )
		{
			this.provider.SetIpolPin( pin, port, ipolState != 0 );
		}

		public void SetIpolPort( byte ipol, Port port )
		{
			this.provider.SetIpolPort( ipol, port );
		}

		public byte GetIpolPort( Port port )
		{
			return this.provider.GetIpolPort( port );
		}

		public byte GetIntFlag( Port port )
		{
			return this.provider.GetIntFlag( port );
		}

		public byte GetIntCap( Port port )
		{
			return this.provider.GetIntCap( port );
		}

		public void SetSPIClockSpeed( uint clock )
		{
			// Bei extern konfiguriertem SpiDevice nicht benötigt.
		}

		public void SoftReset()
		{
			this.provider.ResetDevice();
		}

		public byte I2cConnectionError()
		{
			if( this.useSpi )
			{
				return 0;
			}

			try
			{
				_ = this.ReadRegister( Register.IODIR, Port.A );
				return 0;
			}
			catch
			{
				return 1;
			}
		}

		public void Dispose()
		{
			if( this.disposed )
			{
				return;
			}

			this.disposed = true;

			this.controller.Dispose();
			this.provider.Dispose();
		}

		private static int ToPinIndex( Port port, byte pin )
		{
			return ( port == Port.A ? 0 : 8 ) + pin;
		}

		private static byte ToRegisterAddress( Register register, Port port )
		{
			return (byte)( ( (byte)register * 2 ) + ( port == Port.B ? 1 : 0 ) );
		}

		private GpioPinDriveMode ToDriveMode( PinMode pinState )
		{
			switch( pinState )
			{
				case PinMode.Output:
					return GpioPinDriveMode.Output;
				case PinMode.InputPullUp:
					return GpioPinDriveMode.InputPullUp;
				default:
					return GpioPinDriveMode.Input;
			}
		}

		private void SetOutputLatch( byte olat, Port port )
		{
			this.WriteRegister( Register.OLAT, port, olat );
		}

		private byte ReadRegister( Register register, Port port )
		{
			return this.ReadRegisterRaw( ToRegisterAddress( register, port ) );
		}

		private void WriteRegister( Register register, Port port, byte value )
		{
			this.WriteRegisterRaw( ToRegisterAddress( register, port ), value );
		}

		private byte ReadRegisterRaw( byte register )
		{
			if( this.useSpi )
			{
				return this.ReadSpi( register );
			}

			return this.ReadI2c( register );
		}

		private void WriteRegisterRaw( byte register, byte value )
		{
			if( this.useSpi )
			{
				this.WriteSpi( register, value );
				return;
			}

			this.WriteI2c( register, value );
		}

		private void WriteRegisterRaw( byte register, byte valueA, byte valueB )
		{
			if( this.useSpi )
			{
				this.WriteSpi( register, valueA, valueB );
				return;
			}

			this.WriteI2c( register, valueA, valueB );
		}

		private byte ReadI2c( byte register )
		{
			byte[] buffer = new byte[ 1 ];
			byte[] writeBuffer = new byte[] { register };

			MethodInfo writeReadMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteReadMethod,
				[ "WriteReadPartial", "WriteRead" ],
				typeof( byte[] ),
				typeof( byte[] ) );

			if( writeReadMethod != null )
			{
				writeReadMethod.Invoke( this.i2cDevice, new object[] { writeBuffer, buffer } );
				return buffer[ 0 ];
			}

			MethodInfo writeMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteMethod,
				new string[] { "WritePartial", "Write" },
				typeof( byte[] ) );

			MethodInfo readMethod = this.GetI2cMethod(
				ref this.cachedI2cReadMethod,
				new string[] { "ReadPartial", "Read" },
				typeof( byte[] ) );

			if( writeMethod != null && readMethod != null )
			{
				writeMethod.Invoke( this.i2cDevice, new object[] { writeBuffer } );
				readMethod.Invoke( this.i2cDevice, new object[] { buffer } );
				return buffer[ 0 ];
			}

			throw new NotSupportedException( "Das I2C-Gerät unterstützt kein Lesen." );
		}

		private void WriteI2c( byte register, byte value )
		{
			byte[] buffer = new byte[] { register, value };

			MethodInfo writeMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteMethod,
				new string[] { "WritePartial", "Write" },
				typeof( byte[] ) );

			if( writeMethod != null )
			{
				writeMethod.Invoke( this.i2cDevice, new object[] { buffer } );
				return;
			}

			MethodInfo writeReadMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteReadMethod,
				new string[] { "WriteReadPartial", "WriteRead" },
				typeof( byte[] ),
				typeof( byte[] ) );

			if( writeReadMethod != null )
			{
				byte[] readBuffer = new byte[ 1 ];
				writeReadMethod.Invoke( this.i2cDevice, new object[] { buffer, readBuffer } );
				return;
			}

			throw new NotSupportedException( "Das I2C-Gerät unterstützt kein Schreiben." );
		}

		private void WriteI2c( byte register, byte valueA, byte valueB )
		{
			byte[] buffer = new byte[] { register, valueA, valueB };

			MethodInfo writeMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteMethod,
				new string[] { "WritePartial", "Write" },
				typeof( byte[] ) );

			if( writeMethod != null )
			{
				writeMethod.Invoke( this.i2cDevice, new object[] { buffer } );
				return;
			}

			MethodInfo writeReadMethod = this.GetI2cMethod(
				ref this.cachedI2cWriteReadMethod,
				new string[] { "WriteReadPartial", "WriteRead" },
				typeof( byte[] ),
				typeof( byte[] ) );

			if( writeReadMethod != null )
			{
				byte[] readBuffer = new byte[ 1 ];
				writeReadMethod.Invoke( this.i2cDevice, new object[] { buffer, readBuffer } );
				return;
			}

			throw new NotSupportedException( "Das I2C-Gerät unterstützt kein Schreiben." );
		}

		private MethodInfo GetI2cMethod( ref MethodInfo cache, string[] methodNames, params Type[] parameterTypes )
		{
			if( cache != null )
			{
				return cache;
			}

			Type deviceType = this.i2cDevice != null ? this.i2cDevice.GetType() : null;
			if( deviceType == null )
			{
				return null;
			}

			for( int i = 0; i < methodNames.Length; i++ )
			{
				cache = deviceType.GetMethod( methodNames[ i ], parameterTypes );
				if( cache != null )
				{
					return cache;
				}
			}

			return null;
		}

		private byte ReadSpi( byte register )
		{
			byte[] writeBuffer = new byte[] { this.GetSpiReadOpcode(), register, 0x00 };
			byte[] readBuffer = new byte[ 3 ];

			this.spiDevice.TransferFullDuplex( writeBuffer, readBuffer );
			return readBuffer[ 2 ];
		}

		private void WriteSpi( byte register, byte value )
		{
			this.spiDevice.Write( new byte[] { this.GetSpiWriteOpcode(), register, value } );
		}

		private void WriteSpi( byte register, byte valueA, byte valueB )
		{
			this.spiDevice.Write( new byte[] { this.GetSpiWriteOpcode(), register, valueA, valueB } );
		}

		private byte GetSpiWriteOpcode()
		{
			return (byte)( this.spiAddress << 1 );
		}

		private byte GetSpiReadOpcode()
		{
			return (byte)( ( this.spiAddress << 1 ) | 0x01 );
		}

		private sealed class GpioControllerProvider : IGpioControllerProvider, IDisposable
		{
			private const int PinCountValue = 16;

			private readonly Mcp23017 owner;
			private readonly object syncRoot = new object();
			private readonly bool[] openPins = new bool[ PinCountValue ];
			private readonly GpioPinDriveMode[] driveModes = new GpioPinDriveMode[ PinCountValue ];
			private readonly GpioPinEdge[] valueChangedEdges = new GpioPinEdge[ PinCountValue ];
			private readonly GpioPinValueChangedEventHandler[] valueChangedHandlers = new GpioPinValueChangedEventHandler[ PinCountValue ];
			private readonly TimeSpan[] debounceTimeouts = new TimeSpan[ PinCountValue ];
			private readonly byte[] lastPortValues = new byte[ 2 ];
			private readonly GpioPin interruptPin;
			private readonly object interruptGate = new object();
			private readonly Thread interruptWorker;
			private volatile bool interruptPending;
			private volatile bool disposed;

			public GpioControllerProvider( Mcp23017 owner, GpioPin interruptPin )
			{
				this.owner = owner ?? throw new ArgumentNullException( nameof( owner ) );
				this.interruptPin = interruptPin;

				for( int i = 0; i < PinCountValue; i++ )
				{
					this.driveModes[ i ] = GpioPinDriveMode.Input;
				}

				this.ResetDevice();

				this.lastPortValues[ 0 ] = this.owner.ReadRegister( Register.GPIO, Port.A );
				this.lastPortValues[ 1 ] = this.owner.ReadRegister( Register.GPIO, Port.B );

				if( this.interruptPin != null )
				{
					this.interruptPin.SetDriveMode( GpioPinDriveMode.Input );
					this.interruptPin.ValueChanged += this.OnInterruptPinValueChanged;

					this.interruptWorker = new Thread( this.InterruptWorkerLoop );
					this.interruptWorker.Start();
				}
			}

			public int PinCount
			{
				get { return PinCountValue; }
			}

			public void ResetDevice()
			{
				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					this.ConfigureInterruptHardware();

					this.owner.WriteRegister( Register.IODIR, Port.A, 0xFF );
					this.owner.WriteRegister( Register.IODIR, Port.B, 0xFF );
					this.owner.WriteRegister( Register.GPPU, Port.A, 0x00 );
					this.owner.WriteRegister( Register.GPPU, Port.B, 0x00 );
					this.owner.WriteRegister( Register.GPINTEN, Port.A, 0x00 );
					this.owner.WriteRegister( Register.GPINTEN, Port.B, 0x00 );
					this.owner.WriteRegister( Register.INTCON, Port.A, 0x00 );
					this.owner.WriteRegister( Register.INTCON, Port.B, 0x00 );
					this.owner.WriteRegister( Register.DEFVAL, Port.A, 0x00 );
					this.owner.WriteRegister( Register.DEFVAL, Port.B, 0x00 );
					this.owner.WriteRegister( Register.OLAT, Port.A, 0x00 );
					this.owner.WriteRegister( Register.OLAT, Port.B, 0x00 );

					this.RefreshSnapshot( Port.A );
					this.RefreshSnapshot( Port.B );
					this.RefreshInterruptConfigurationForPort( Port.A );
					this.RefreshInterruptConfigurationForPort( Port.B );

					lock( this.interruptGate )
					{
						this.interruptPending = false;
					}
				}
			}

			public void OpenPin( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					if( this.openPins[ pin ] )
					{
						throw new InvalidOperationException( $"Pin {pin} ist bereits geöffnet." );
					}

					this.openPins[ pin ] = true;
				}
			}

			public void ClosePin( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					if( !this.openPins[ pin ] )
					{
						throw new InvalidOperationException( $"Pin {pin} ist nicht geöffnet." );
					}

					this.openPins[ pin ] = false;
					this.driveModes[ pin ] = GpioPinDriveMode.Input;
					this.valueChangedHandlers[ pin ] = null;
					this.valueChangedEdges[ pin ] = 0;
					this.RefreshInterruptConfigurationForPort( this.ToPort( pin ) );
				}
			}

			public bool IsDriveModeSupported( int pin, GpioPinDriveMode mode )
			{
				this.ValidatePin( pin );
				return mode == GpioPinDriveMode.Input ||
				       mode == GpioPinDriveMode.InputPullUp ||
				       mode == GpioPinDriveMode.Output;
			}

			public void SetPinChangedHandler( int pin, GpioPinEdge edge, GpioPinValueChangedEventHandler value )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					this.valueChangedEdges[ pin ] = edge;
					this.valueChangedHandlers[ pin ] = value;
					this.RefreshInterruptConfigurationForPort( this.ToPort( pin ) );
				}
			}

			public void ClearPinChangedHandler( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					this.valueChangedEdges[ pin ] = 0;
					this.valueChangedHandlers[ pin ] = null;
					this.RefreshInterruptConfigurationForPort( this.ToPort( pin ) );
				}
			}

			public void SetDriveMode( int pin, GpioPinDriveMode mode )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					if( !this.IsDriveModeSupported( pin, mode ) )
					{
						throw new NotSupportedException( $"Der DriveMode {mode} wird vom MCP23017 nicht unterstützt." );
					}

					this.driveModes[ pin ] = mode;

					Port port = this.ToPort( pin );
					byte mask = this.ToMask( pin );
					byte iodir = this.owner.ReadRegister( Register.IODIR, port );
					byte gppu = this.owner.ReadRegister( Register.GPPU, port );

					if( mode == GpioPinDriveMode.Output )
					{
						iodir &= (byte)~mask;
						gppu &= (byte)~mask;
					}
					else if( mode == GpioPinDriveMode.InputPullUp )
					{
						iodir |= mask;
						gppu |= mask;
					}
					else
					{
						iodir |= mask;
						gppu &= (byte)~mask;
					}

					this.owner.WriteRegister( Register.IODIR, port, iodir );
					this.owner.WriteRegister( Register.GPPU, port, gppu );

					this.RefreshSnapshot( port );
					this.RefreshInterruptConfigurationForPort( port );
				}
			}

			public GpioPinDriveMode GetDriveMode( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );
					return this.driveModes[ pin ];
				}
			}

			public void SetPortMode( byte portState, Port port )
			{
				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					byte iodir = unchecked( (byte)~portState );

					this.owner.WriteRegister( Register.IODIR, port, iodir );
					this.owner.WriteRegister( Register.GPPU, port, 0x00 );

					for( int bit = 0; bit < 8; bit++ )
					{
						int pin = this.ToPinIndex( port, bit );
						bool output = ( portState & ( 1 << bit ) ) != 0;
						this.driveModes[ pin ] = output ? GpioPinDriveMode.Output : GpioPinDriveMode.Input;
					}

					this.RefreshSnapshot( port );
					this.RefreshInterruptConfigurationForPort( port );
				}
			}

			public void SetPortMode( byte portState, Port port, PinMode pu )
			{
				if( pu != PinMode.InputPullUp )
				{
					return;
				}

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					byte iodir = unchecked( (byte)~portState );
					byte gppu = iodir;

					this.owner.WriteRegister( Register.IODIR, port, iodir );
					this.owner.WriteRegister( Register.GPPU, port, gppu );

					for( int bit = 0; bit < 8; bit++ )
					{
						int pin = this.ToPinIndex( port, bit );
						bool output = ( portState & ( 1 << bit ) ) != 0;
						this.driveModes[ pin ] = output ? GpioPinDriveMode.Output : GpioPinDriveMode.InputPullUp;
					}

					this.RefreshSnapshot( port );
					this.RefreshInterruptConfigurationForPort( port );
				}
			}

			public GpioPinValue Read( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					Port port = this.ToPort( pin );
					byte value = this.owner.ReadRegister( Register.GPIO, port );
					return ( ( value >> this.ToBitIndex( pin ) ) & 0x01 ) != 0 ? GpioPinValue.High : GpioPinValue.Low;
				}
			}

			public void Write( int pin, GpioPinValue value )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );

					if( this.driveModes[ pin ] != GpioPinDriveMode.Output )
					{
						throw new InvalidOperationException( $"Pin {pin} ist nicht als Ausgang konfiguriert." );
					}

					Port port = this.ToPort( pin );
					byte mask = this.ToMask( pin );
					byte olat = this.owner.ReadRegister( Register.OLAT, port );

					if( value == GpioPinValue.High )
					{
						olat |= mask;
					}
					else
					{
						olat &= (byte)~mask;
					}

					this.owner.WriteRegister( Register.OLAT, port, olat );
					this.RefreshSnapshot( port );
				}
			}

			public void TransferFeature( int pinSource, int pinDestination, uint mode, uint type, uint direction, uint speed, uint alternate )
			{
				throw new NotSupportedException( "Der MCP23017 unterstützt keine Alternate-Functions." );
			}

			public TimeSpan GetDebounceTimeout( int pin )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );
					return this.debounceTimeouts[ pin ];
				}
			}

			public void SetDebounceTimeout( int pin, TimeSpan value )
			{
				this.ValidatePin( pin );

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();
					this.RequireOpenPin( pin );
					this.debounceTimeouts[ pin ] = value;
				}
			}

			public void Dispose()
			{
				if( this.disposed )
				{
					return;
				}

				lock( this.syncRoot )
				{
					if( this.disposed )
					{
						return;
					}

					this.disposed = true;

					if( this.interruptPin != null )
					{
						this.interruptPin.ValueChanged -= this.OnInterruptPinValueChanged;
					}
				}

				lock( this.interruptGate )
				{
					this.interruptPending = true;
				}
			}

			private void OnInterruptPinValueChanged( object sender, GpioPinValueChangedEventArgs e )
			{
				if( e.Edge != GpioPinEdge.RisingEdge )
				{
					return;
				}

				lock( this.interruptGate )
				{
					if( this.disposed )
					{
						return;
					}

					this.interruptPending = true;
				}
			}

			private void InterruptWorkerLoop()
			{
				while( !this.disposed )
				{
					try
					{
						if( !this.ShouldProcessInterrupts() )
						{
							Thread.Sleep( 1 );
							continue;
						}

						this.ProcessInterrupts();
					}
					catch
					{
						Thread.Sleep( 1 );
					}
				}
			}

			private bool ShouldProcessInterrupts()
			{
				lock( this.interruptGate )
				{
					if( this.interruptPending )
					{
						return true;
					}
				}

				return this.interruptPin != null && this.interruptPin.Read() == GpioPinValue.High;
			}

			private void ProcessInterrupts()
			{
				while( !this.disposed )
				{
					bool hadPending = this.TakePendingInterrupt();
					if( !hadPending && ( this.interruptPin == null || this.interruptPin.Read() == GpioPinValue.Low ) )
					{
						return;
					}

					if( !this.ServiceInterruptsOnce() )
					{
						if( this.interruptPin == null || this.interruptPin.Read() == GpioPinValue.Low )
						{
							return;
						}

						Thread.Sleep( 1 );
					}
				}
			}

			private bool TakePendingInterrupt()
			{
				lock( this.interruptGate )
				{
					if( !this.interruptPending )
					{
						return false;
					}

					this.interruptPending = false;
					return true;
				}
			}

			private bool ServiceInterruptsOnce()
			{
				GpioPinValueChangedEventHandler[] handlers = new GpioPinValueChangedEventHandler[ PinCountValue ];
				GpioPinEdge[] edges = new GpioPinEdge[ PinCountValue ];
				int count = 0;

				lock( this.syncRoot )
				{
					this.EnsureNotDisposed();

					byte capturedA = this.owner.ReadRegister( Register.INTCAP, Port.A );
					byte currentA = this.owner.ReadRegister( Register.GPIO, Port.A );
					byte intfA = this.owner.ReadRegister( Register.INTF, Port.A );

					this.CollectChangedPinsFromInterruptStatus(
						Port.A,
						intfA,
						this.lastPortValues[ 0 ],
						capturedA,
						handlers,
						edges,
						ref count );

					if( count == 0 )
					{
						// Fallback, falls INTF im Timing-Fall schon wieder 0 ist
						this.CollectChangedPinsFromInterruptStatus(
							Port.A,
							0xFF,
							this.lastPortValues[ 0 ],
							currentA,
							handlers,
							edges,
							ref count );
					}

					this.lastPortValues[ 0 ] = currentA;
				}

				for( int i = 0; i < count; i++ )
				{
					handlers[ i ]( null, new GpioPinValueChangedEventArgs( edges[ i ], DateTime.UtcNow ) );
				}

				return count > 0;
			}

			private void CollectChangedPinsFromInterruptStatus(
				Port port,
				byte intf,
				byte previous,
				byte captured,
				GpioPinValueChangedEventHandler[] handlers,
				GpioPinEdge[] edges,
				ref int count )
			{
				for( int bit = 0; bit < 8; bit++ )
				{
					byte mask = ( byte )( 1 << bit );

					if( ( intf & mask ) == 0 )
					{
						continue;
					}

					bool previousHigh = ( previous & mask ) != 0;
					bool capturedHigh = ( captured & mask ) != 0;

					if( previousHigh == capturedHigh )
					{
						continue;
					}

					int pin = this.ToPinIndex( port, bit );
					GpioPinValueChangedEventHandler handler = this.valueChangedHandlers[ pin ];
					if( handler == null )
					{
						continue;
					}

					GpioPinEdge actualEdge = capturedHigh ? GpioPinEdge.RisingEdge : GpioPinEdge.FallingEdge;

					handlers[ count ] = handler;
					edges[ count ] = actualEdge;
					count++;
				}
			}
			private void RefreshInterruptConfigurationForPort( Port port )
			{
				byte gpinten = 0x00;
				byte intcon = 0x00;

				for( int bit = 0; bit < 8; bit++ )
				{
					int pin = this.ToPinIndex( port, bit );
					bool enabled =
						this.openPins[ pin ] &&
						this.valueChangedHandlers[ pin ] != null &&
						( this.driveModes[ pin ] == GpioPinDriveMode.Input || this.driveModes[ pin ] == GpioPinDriveMode.InputPullUp );

					if( enabled )
					{
						gpinten |= (byte)( 1 << bit );
					}
				}

				this.owner.WriteRegister( Register.GPINTEN, port, gpinten );
				this.owner.WriteRegister( Register.INTCON, port, intcon );
			}

			private void ConfigureInterruptHardware()
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( this.interruptPin != null )
				{
					// INT A als gemeinsame Interrupt-Leitung: active-high, push-pull, mirrored.
					ioConA |= IoConMirrorMask;
					ioConB |= IoConMirrorMask;

					ioConA |= IoConIntPolMask;
					ioConB |= IoConIntPolMask;

					ioConA &= unchecked((byte)~IoConOdrMask);
					ioConB &= unchecked((byte)~IoConOdrMask);
				}
				else
				{
					ioConA &= unchecked((byte)~IoConMirrorMask);
					ioConB &= unchecked((byte)~IoConMirrorMask);
					ioConA &= unchecked((byte)~IoConIntPolMask);
					ioConB &= unchecked((byte)~IoConIntPolMask);
					ioConA &= unchecked((byte)~IoConOdrMask);
					ioConB &= unchecked((byte)~IoConOdrMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			private void RefreshSnapshot( Port port )
			{
				if( port == Port.A )
				{
					this.lastPortValues[ 0 ] = this.owner.ReadRegister( Register.GPIO, Port.A );
				}
				else
				{
					this.lastPortValues[ 1 ] = this.owner.ReadRegister( Register.GPIO, Port.B );
				}
			}

			private int ToPinIndex( Port port, int bit )
			{
				return ( port == Port.A ? 0 : 8 ) + bit;
			}

			private Port ToPort( int pin )
			{
				return pin < 8 ? Port.A : Port.B;
			}

			private int ToBitIndex( int pin )
			{
				return pin % 8;
			}

			private byte ToMask( int pin )
			{
				return (byte)( 1 << this.ToBitIndex( pin ) );
			}

			private void RequireOpenPin( int pin )
			{
				if( !this.openPins[ pin ] )
				{
					throw new InvalidOperationException( $"Pin {pin} ist nicht geöffnet." );
				}
			}

			private void EnsureNotDisposed()
			{
				if( this.disposed )
				{
					throw new ObjectDisposedException( nameof( Mcp23017 ) );
				}
			}

			private void ValidatePin( int pin )
			{
				if( pin < 0 || pin >= PinCountValue )
				{
					throw new ArgumentOutOfRangeException( nameof( pin ), "Der Pin muss zwischen 0 und 15 liegen." );
				}
			}

			public void SetInterruptPinPol( byte level )
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( level != 0 )
				{
					ioConA |= IoConIntPolMask;
					ioConB |= IoConIntPolMask;
				}
				else
				{
					ioConA &= unchecked((byte)~IoConIntPolMask);
					ioConB &= unchecked((byte)~IoConIntPolMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			public void SetIntOdr( byte openDrain )
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( openDrain != 0 )
				{
					ioConA |= IoConOdrMask;
					ioConB |= IoConOdrMask;
				}
				else
				{
					ioConA &= unchecked((byte)~IoConOdrMask);
					ioConB &= unchecked((byte)~IoConOdrMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			public void SetIntMirror( byte mirrored )
			{
				byte ioConA = this.owner.ReadRegister( Register.IOCON, Port.A );
				byte ioConB = this.owner.ReadRegister( Register.IOCON, Port.B );

				if( mirrored != 0 )
				{
					ioConA |= IoConMirrorMask;
					ioConB |= IoConMirrorMask;
				}
				else
				{
					ioConA &= unchecked((byte)~IoConMirrorMask);
					ioConB &= unchecked((byte)~IoConMirrorMask);
				}

				this.owner.WriteRegister( Register.IOCON, Port.A, ioConA );
				this.owner.WriteRegister( Register.IOCON, Port.B, ioConB );
			}

			public void SetInterruptOnChangePin( byte pin, Port port )
			{
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte mask = (byte)( 1 << pin );

				iodir |= mask;
				gpIntEn |= mask;
				intCon &= (byte)~mask;

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
			}

			public void SetInterruptOnDefValDevPin( byte pin, Port port, byte pinIntLevel )
			{
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );
				byte defVal = this.owner.ReadRegister( Register.DEFVAL, port );
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte mask = (byte)( 1 << pin );

				iodir |= mask;
				gpIntEn |= mask;
				intCon |= mask;

				if( pinIntLevel != 0 )
				{
					defVal |= mask;
				}
				else
				{
					defVal &= (byte)~mask;
				}

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
				this.owner.WriteRegister( Register.DEFVAL, port, defVal );
			}

			public void SetInterruptOnChangePort( byte intOnChangePins, Port port )
			{
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );

				iodir |= intOnChangePins;
				gpIntEn |= intOnChangePins;
				intCon &= (byte)~intOnChangePins;

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
			}

			public void SetInterruptOnDefValDevPort( byte intPins, Port port, byte defVal )
			{
				byte iodir = this.owner.ReadRegister( Register.IODIR, port );
				byte gpIntEn = this.owner.ReadRegister( Register.GPINTEN, port );
				byte intCon = this.owner.ReadRegister( Register.INTCON, port );

				iodir |= intPins;
				gpIntEn |= intPins;
				intCon |= intPins;

				this.owner.WriteRegister( Register.IODIR, port, iodir );
				this.owner.WriteRegister( Register.GPINTEN, port, gpIntEn );
				this.owner.WriteRegister( Register.INTCON, port, intCon );
				this.owner.WriteRegister( Register.DEFVAL, port, defVal );
			}

			public void DeleteAllInterruptsOnPort( Port port )
			{
				this.owner.WriteRegister( Register.GPINTEN, port, 0x00 );
				this.owner.WriteRegister( Register.INTCON, port, 0x00 );
			}

			public void SetIpolPin( byte pin, Port port, bool ipolState )
			{
				byte ipolPortState = this.GetIpolPort( port );
				byte mask = (byte)( 1 << pin );

				if( ipolState )
				{
					ipolPortState |= mask;
				}
				else
				{
					ipolPortState &= (byte)~mask;
				}

				this.SetIpolPort( ipolPortState, port );
			}

			public void SetIpolPort( byte ipol, Port port )
			{
				this.owner.WriteRegister( Register.IPOL, port, ipol );
			}

			public byte GetIpolPort( Port port )
			{
				return this.owner.ReadRegister( Register.IPOL, port );
			}

			public byte GetIntFlag( Port port )
			{
				return this.owner.ReadRegister( Register.INTF, port );
			}

			public byte GetIntCap( Port port )
			{
				return this.owner.ReadRegister( Register.INTCAP, port );
			}
		}
	}
}
