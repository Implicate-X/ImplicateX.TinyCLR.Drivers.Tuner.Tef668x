using System;
using System.Collections;
using System.Text;
using System.Threading;

namespace ImplicateX.TinyCLR.Drivers.Expander
{
	public partial class Mcp23017
	{
		/// <summary>
		/// The register addresses of the MCP23017.
		/// </summary>
		public enum Register : byte
		{
			/// <summary>
			/// Controls the direction of the data I/O. When a bit is set, the corresponding pin becomes an input.<br/>
			/// When a bit is clear, the corresponding pin becomes an output.
			/// </summary>
			/// <remarks>On reset/power on all bits are set (all pins are input).</remarks>
			IODIR,
			//
			/// <summary>
			/// Configures the polarity on the corresponding GPIO port bits. When a bit is set,
			/// the corresponding GPIO register bit will reflect the inverted value on the pin.
			/// </summary>
			IPOL,
			//
			/// <summary>
			/// Controls the interrupt-on-change feature for each pin. When a bit is set, the
			/// corresponding pin is enabled for interrupt-on-change. The DEFVAL and INTCON registers
			/// must also be configured if any pins are enabled for interrupt-on-change.
			/// </summary>
			GPINTEN,
			//
			/// <summary>
			/// Configures the default comparison value. If enabled (via GPINTEN and INTCON)
			/// to compare against the DEFVAL register, an opposite value on the associated pin
			/// will cause an interrupt to occur.
			/// </summary>
			DEFVAL,
			//
			/// <summary>
			/// Controls how the associated pin value is compared for the interrupt-on-change
			/// feature. When a bit is set, the corresponding I/O pin is compared against the
			/// associated bit in the DEFVAL register. When a bit value is clear, the corresponding
			/// I/O pin is compared against the previous value.
			/// </summary>
			INTCON,
			//
			/// <summary>
			/// Contains several bits for configuring the device. See respective datasheet for
			/// more details.
			/// </summary>
			IOCON,
			//
			/// <summary>
			/// Controls the pull-up resistors for the port pins. When a bit is set and the corresponding
			/// pin is configured as an input, the corresponding port pin is internally pulled
			/// up with a 100 kΩ resistor.
			/// </summary>
			GPPU,
			//
			/// <summary>
			/// Reflects the interrupt condition on the port pins of any pin that is enabled
			/// for interrupts via the GPINTEN register. A 'set' bit indicates that the associated
			/// pin caused the interrupt. This register is read-only. Writes to this register
			/// will be ignored.
			/// </summary>
			INTF,
			//
			/// <summary>
			/// The INTCAP register captures the GPIO port value at the time the interrupt occurred.
			/// The register is read-only and is updated only when an interrupt occurs. The register
			/// will remain unchanged until the interrupt is cleared via a read of INTCAP or
			/// GPIO.
			/// </summary>
			INTCAP,
			//
			/// <summary>
			/// Reflects the value on the port. Reading from this register reads the port. Writing
			/// to this register modifies the Output Latch (OLAT) register.
			/// </summary>
			GPIO,
			//
			/// <summary>
			/// Provides access to the output latches. A read from this register results in a
			/// read of the OLAT and not the port itself. A write to this register modifies the
			/// output latches that modify the pins configured as outputs.
			/// </summary>
			/// <remarks>
			/// On reset/power on all bits are not set (all pins are low).
			/// </remarks>
			OLAT
		}
	}
}
