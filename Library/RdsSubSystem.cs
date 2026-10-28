using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GHIElectronics.TinyCLR.Devices.Gpio;

namespace ImplicateX.TinyCLR.Drivers.Tuner
{
	public partial class Tef668x
	{
		/// <summary>
		/// Provides an event-driven RDS processing subsystem for a TEF668x tuner, capturing GPIO data-available signals,
		/// reading and queuing RDS frames, and updating a decoder for display-friendly output.
		/// </summary>
		/// <remarks>Runs a background worker that responds to pin-change interrupts and periodic watchdog checks,
		/// supports configurable active-high or active-low data-available signaling, raises frame notifications, and keeps a
		/// bounded in-memory frame queue.</remarks>
		/// <remarks>
		/// Initializes a new instance of the RDS subsystem with the tuner interface, RDS data-available pin, and signal
		/// polarity.
		/// </remarks>
		/// <remarks>Use <paramref name="dataAvailableActiveHigh"/> to match the configured polarity of the hardware
		/// data-available signal.</remarks>
		/// <param name="tuner">Tuner interface used to read RDS data from the device.</param>
		/// <param name="rdsPin">GPIO pin that signals RDS data availability.</param>
		/// <param name="dataAvailableActiveHigh">Indicates whether the data-available signal is active high.</param>
		/// <exception cref="ArgumentNullException">Thrown when <paramref name="tuner"/> or <paramref name="rdsPin"/> is <see langword="null"/>.</exception>
		public sealed class RdsSubSystem( Tef668x tuner, GpioPin rdsPin, bool dataAvailableActiveHigh = true ) : IDisposable
		{
			private const int maxQueuedFrames = 64;
			private const int readSettleDelayMs = 2;
			private const int emptyDataRetryCount = 3;
			private const int watchdogIntervalMs = 250;

			private readonly Tef668x tuner = tuner ?? throw new ArgumentNullException( nameof( tuner ) );
			private readonly GpioPin rdsPin = rdsPin ?? throw new ArgumentNullException( nameof( rdsPin ) );
			private readonly bool dataAvailableActiveHigh = dataAvailableActiveHigh;
			private readonly List<RdsFrame> frameQueue = new List<RdsFrame>( maxQueuedFrames );
			private readonly object syncRoot = new object();
			private readonly AutoResetEvent workerSignal = new AutoResetEvent( false );

			private Thread workerThread = null!;
			private bool runWorker;
			private bool disposed;
			private bool interruptPending;

			/// <summary>
			/// Gets the RDS decoder used to parse incoming RDS data.
			/// </summary>
			/// <remarks>Initialized once and exposed as a read-only reference.</remarks>
			public RdsDecoder Decoder { get; } = new RdsDecoder();

			/// <summary>
			/// Gets a value indicating whether the worker is currently running.
			/// </summary>
			/// <remarks>Access to the underlying state is synchronized.</remarks>
			public bool IsRunning
			{
				get
				{
					lock( syncRoot )
					{
						return runWorker;
					}
				}
			}

			public event Action<RdsFrame> FrameReceived = null!;

			/// <summary>
			/// Configures the tuner RDS decoder to enabled mode with a decoder restart and DAVN pin signaling on the RDS
			/// interface.
			/// </summary>
			/// <remarks>Uses RDS pin interface 2, which maps the RDS pin to the active-low data-available status output
			/// (DAVN).</remarks>
			public void ConfigureDecoderMode()
			{
				tuner.SetRds( mode: 1, restart: 2, pinInterface: 2 );
			}

			/// <summary>
			/// Attempts to remove and return the next RDS frame from the queue.
			/// </summary>
			/// <remarks>Access to the queue is synchronized.</remarks>
			/// <param name="frame">When this method returns <see langword="true"/>, contains the dequeued <see cref="RdsFrame"/>; otherwise, <see
			/// langword="null"/>.</param>
			/// <returns><see langword="true"/> if a frame was dequeued; <see langword="false"/> if the queue is empty.</returns>
			public bool TryDequeueRdsFrame( out RdsFrame frame )
			{
				lock( syncRoot )
				{
					if( frameQueue.Count == 0 )
					{
						frame = null!;
						return false;
					}

					frame = frameQueue[ 0 ];
					frameQueue.RemoveAt( 0 );
					return true;
				}
			}

			/// <summary>
			/// Creates an RDS display model from the current decoder state.
			/// </summary>
			/// <remarks>AfList is created as a new list instance from the decoder's alternative frequencies.</remarks>
			/// <returns>An RdsDisplayModel populated with program service name, radio text, program type text, region code, traffic
			/// program and announcement flags, and a copy of the alternative frequencies.</returns>
			public RdsDisplayModel GetDisplayModel()
			{
				return new RdsDisplayModel
				{
					PsName = Decoder.ProgramServiceName,
					RadioText = Decoder.RadioText,
					PtyText = Decoder.ProgramTypeText,
					PiRegion = Decoder.RegionCode,
					Tp = Decoder.TrafficProgram,
					Ta = Decoder.TrafficAnnouncement,
					AfList = new List<double>( Decoder.AlternativeFrequencies )
				};
			}

			/// <summary>
			/// Starts processing by initializing the decoder path, subscribing to RDS pin notifications, launching the
			/// background worker thread, and signaling it to run.
			/// </summary>
			/// <remarks>Thread-safe and idempotent; if processing is already running, the call returns without starting
			/// another worker. Throws if the instance has been disposed.</remarks>
			public void Start()
			{
				lock( syncRoot )
				{
					ThrowIfDisposed();

					if( runWorker )
					{
						return;
					}

					runWorker = true;
					interruptPending = true;
					workerThread = new Thread( WorkerLoop );
				}

				ConfigureDecoderMode();

				rdsPin.ValueChanged += OnRdsPinValueChanged;

				workerThread.Start();
				workerSignal.Set();
			}

			/// <summary>
			/// Stops background RDS processing and waits for the worker thread to exit.
			/// </summary>
			/// <remarks>If processing is already stopped, returns immediately. Clears the running state under
			/// synchronization, unsubscribes from RDS pin change notifications, signals the worker to wake, and joins the
			/// previous worker thread before returning.</remarks>
			public void Stop()
			{
				Thread threadToJoin = null!;

				lock( syncRoot )
				{
					if( !runWorker )
					{
						return;
					}

					runWorker = false;
					threadToJoin = workerThread;
					workerThread = null!;
				}

				rdsPin.ValueChanged -= OnRdsPinValueChanged;
				workerSignal.Set();
				threadToJoin?.Join();
			}

			/// <summary>
			/// Releases resources used by the instance by stopping ongoing operations and marking the object as disposed.
			/// </summary>
			/// <remarks>Safe to call multiple times; subsequent calls return without performing additional
			/// work.</remarks>
			public void Dispose()
			{
				if( disposed )
				{
					return;
				}

				Stop();

				disposed = true;
			}

			/// <summary>
			/// Runs the background loop that waits for worker signals or watchdog timeouts, detects pending RDS data, and drains
			/// available RDS frames until the worker is stopped.
			/// </summary>
			/// <remarks>Combines interrupt-driven servicing with watchdog polling to recover when an IRQ edge is
			/// missed, clears the pending-interrupt flag under synchronization, and applies a short settle delay before reading
			/// data.</remarks>
			private void WorkerLoop()
			{
				while( true )
				{
					bool signaled = workerSignal.WaitOne( watchdogIntervalMs, false );

					if( !runWorker )
					{
						return;
					}

					bool shouldService;
					lock( syncRoot )
					{
						shouldService = interruptPending;
						interruptPending = false;
					}

					if( !shouldService && !signaled )
					{
						if( IsDataAvailableActive() )
						{
							shouldService = true;
						}
						else if( tuner.TryGetRdsStatus( out RdsFrame statusFrame ) && ( statusFrame.HasData || statusFrame.HasDataLoss ) )
						{
							shouldService = true;
							Debug.WriteLine( "RDS watchdog: pending data detected without IRQ edge." );
						}
					}

					if( shouldService || IsDataAvailableActive() )
					{
						Thread.Sleep( readSettleDelayMs );
						DrainRdsData();
					}
				}
			}

			/// <summary>
			/// Handles RDS pin value changes by queuing interrupt processing and signaling the worker thread when processing is
			/// active.
			/// </summary>
			/// <remarks>Used as the GPIO callback path for RDS data-available signaling; the handler only flags pending
			/// work and wakes the worker for deferred processing.</remarks>
			/// <param name="sender">The source of the GPIO value-changed event.</param>
			/// <param name="e">Event data for the GPIO pin value change.</param>
			private void OnRdsPinValueChanged( object sender, GpioPinValueChangedEventArgs e )
			{
				if( !runWorker )
				{
					return;
				}

				lock( syncRoot )
				{
					interruptPending = true;
				}

				workerSignal.Set();
			}

			/// <summary>
			/// Determines whether the RDS data-available signal is currently asserted.
			/// </summary>
			/// <remarks>Compares the current GPIO pin state to the configured active level so either active-high or
			/// active-low signaling can be used.</remarks>
			/// <returns>true if the RDS pin matches the configured active polarity; otherwise, false.</returns>
			private bool IsDataAvailableActive()
			{
				GpioPinValue value = rdsPin.Read();
				return dataAvailableActiveHigh ? value == GpioPinValue.High : value == GpioPinValue.Low;
			}

			/// <summary>
			/// Reads pending RDS frames from the tuner, enqueues frames that contain data or data-loss indicators, and signals
			/// another processing pass when the data-available line remains active.
			/// </summary>
			/// <remarks>Per invocation, processing is limited to a fixed number of reads. If no data has been observed
			/// yet and data-available is still active, it performs short, bounded retries to allow hardware settle time before
			/// exiting.</remarks>
			private void DrainRdsData()
			{
				const int maxReadsPerService = 24;
				bool sawData = false;
				int emptyRetries = 0;

				for( int i = 0; i < maxReadsPerService; i++ )
				{
					if( !tuner.TryGetRdsData( out RdsFrame frame ) )
					{
						if( IsDataAvailableActive() )
						{
							lock( syncRoot )
							{
								interruptPending = true;
							}

							workerSignal.Set();
						}

						return;
					}

					if( frame.HasData || frame.HasDataLoss )
					{
						sawData = sawData || frame.HasData;
						emptyRetries = 0;
						EnqueueFrame( frame );
					}

					if( !frame.HasData )
					{
						if( !sawData && IsDataAvailableActive() && emptyRetries < emptyDataRetryCount )
						{
							emptyRetries++;
							Thread.Sleep( readSettleDelayMs );
							continue;
						}

						return;
					}
				}

				if( IsDataAvailableActive() )
				{
					lock( syncRoot )
					{
						interruptPending = true;
					}

					workerSignal.Set();
				}
			}

			/// <summary>
			/// Adds an RDS frame to the internal bounded queue, processes it with the decoder, and raises the frame-received
			/// notification.
			/// </summary>
			/// <remarks>When the queue reaches capacity, the oldest frame is removed before adding the new frame.
			/// Access to the queue is synchronized.</remarks>
			/// <param name="frame">The RDS frame to queue and process.</param>
			private void EnqueueFrame( RdsFrame frame )
			{
				if( frame.HasDataLoss )
				{
					Debug.WriteLine( $"RDS LOSS detected (status[14]=1): status=0x{frame.Status:X4} PI=0x{frame.BlockA:X4}" );
				}

				lock( syncRoot )
				{
					if( frameQueue.Count >= maxQueuedFrames )
					{
						frameQueue.RemoveAt( 0 );
					}

					frameQueue.Add( frame );
				}

				Decoder.ProcessFrame( frame );
				FrameReceived?.Invoke( frame );
			}

			/// <summary>
			/// Throws an ObjectDisposedException when the RdsSubSystem instance has been disposed.
			/// </summary>
			/// <exception cref="ObjectDisposedException">Thrown when the RdsSubSystem instance has been disposed.</exception>
			private void ThrowIfDisposed()
			{
				if( disposed )
				{
					throw new ObjectDisposedException( nameof( RdsSubSystem ) );
				}
			}
		}
	}
}
