using System;
using System.Collections.Generic;

namespace ImplicateX.TinyCLR.Drivers.Tuner
{
	public partial class Tef668x
	{
		/// <summary>
		/// Represents decoded RDS information for display, including program service name, radio text, program type, PI
		/// region, traffic flags, and alternative frequencies.
		/// </summary>
		public sealed class RdsDisplayModel
		{
			/// <summary>
			/// Gets or sets the Program Service (PS) name.
			/// </summary>
			public string PsName { get; set; }
			/// <summary>
			/// Gets or sets the Radio Text (RT).
			/// </summary>
			public string RadioText { get; set; }
			/// <summary>
			/// Gets or sets the Program Type (PTY) text.
			/// </summary>
			public string PtyText { get; set; }
			/// <summary>
			/// Gets or sets the PI region code.
			/// </summary>
			public string PiRegion { get; set; }
			/// <summary>
			/// Gets or sets a value indicating whether the station is a Traffic Program (TP).
			/// </summary>
			public bool Tp { get; set; }
			/// <summary>
			/// Gets or sets a value indicating whether a Traffic Announcement (TA) is active.
			/// </summary>
			public bool Ta { get; set; }
			/// <summary>
			/// Gets or sets the list of Alternative Frequencies (AF).
			/// </summary>
			public IList<double> AfList { get; set; }
		}

		/// <summary>
		/// Decodes Radio Data System (RDS) frames and publishes station metadata, radio text, alternative frequencies, and
		/// traffic status.
		/// </summary>
		/// <remarks>Maintains decoder state across frames, validates repeated segment data before accepting updates,
		/// and raises events when decoded values change. Resets text state when synchronization is lost or when the program
		/// identifier changes.</remarks>
		public sealed class RdsDecoder
		{
			private readonly char[] psBuffer = new char[ 8 ];
			private readonly char[] rtBuffer = new char[ 64 ];
			private readonly bool[] psReceived = new bool[ 4 ];
			private readonly bool[] rtReceived = new bool[ 16 ];
			private readonly ushort[] psLastWords = new ushort[ 4 ];
			private readonly bool[] psLastWordValid = new bool[ 4 ];
			private readonly ushort[] rtLastBlockC = new ushort[ 16 ];
			private readonly ushort[] rtLastBlockD = new ushort[ 16 ];
			private readonly bool[] rtLastWordValid = new bool[ 16 ];

			private readonly List<double> afFrequencies = new List<double>();
			private ushort lastProgramIdentifier;
			private bool rtTextAbValid;
			private bool rtTextAbFlag;
			private bool rtVersionA = true;

			public string ProgramServiceName { get; private set; }
			public string RadioText { get; private set; }
			public ushort ProgramIdentifier { get; private set; }
			public int ProgramType { get; private set; }
			public bool TrafficProgram { get; private set; }
			public bool TrafficAnnouncement { get; private set; }
			public string ProgramTypeText => GetPtyText( ProgramType );
			public string RegionCode => GetPiRegion( ProgramIdentifier );
			public IList<double> AlternativeFrequencies => afFrequencies;

			public event Action<string> PsUpdated;
			public event Action<string> RtUpdated;
			public event Action<IList<double>> AfUpdated;
			public event Action<bool, bool> TrafficStatusUpdated;

			/// <summary>
			/// Processes a single RDS frame, updates decoder state and program metadata, raises traffic status notifications,
			/// and dispatches supported group types for PS/AF and RadioText decoding.
			/// </summary>
			/// <remarks>Returns without processing when the frame is null, contains no data, or is marked as first-PI
			/// data. Resets decoder state when synchronization is lost or when the program identifier changes.</remarks>
			/// <param name="frame">RDS frame to process.</param>
			public void ProcessFrame( Tef668x.RdsFrame frame )
			{
				if( frame == null || ( !frame.HasData && !frame.HasDataLoss ) )
					return;

				if( !frame.IsSynchronized )
				{
					ResetDecoderState( clearAlternativeFrequencies: false );
					return;
				}

				ushort blockA = frame.BlockA;
				ushort blockB = frame.BlockB;
				ushort blockC = frame.BlockC;
				ushort blockD = frame.BlockD;

				if( lastProgramIdentifier != 0 && blockA != 0 && blockA != lastProgramIdentifier )
				{
					ResetDecoderState( clearAlternativeFrequencies: true );
				}

				ProgramIdentifier = blockA;
				lastProgramIdentifier = ProgramIdentifier;

				if( frame.IsFirstPiData )
				{
					return;
				}

				int groupType = ( blockB >> 12 ) & 0xF;
				bool versionA = ( ( blockB >> 11 ) & 1 ) == 0;
				ProgramType = ( blockB >> 5 ) & 0x1F;
				TrafficProgram = ( ( blockB >> 10 ) & 1 ) != 0;

				if( groupType == 0 )
				{
					TrafficAnnouncement = ( ( blockB >> 4 ) & 1 ) != 0;
				}

				TrafficStatusUpdated?.Invoke( TrafficProgram, TrafficAnnouncement );

				int textIndex = blockB & 0x1F;

				switch( groupType )
				{
					case 0:
						DecodePs( blockD, textIndex & 0x03 );
						DecodeAf( blockC );
						break;

					case 2:
						bool textAbFlag = ( ( blockB >> 4 ) & 1 ) != 0;
						EnsureRtMessageContext( versionA, textAbFlag );
						DecodeRt( blockC, blockD, textIndex & 0x0F, versionA );
						break;

					default:
						break;
				}
			}

			/// <summary>
			/// Decodes and commits a Program Service (PS) name segment from an RDS block after repeat validation, then updates
			/// the full PS name when all segments are available.
			/// </summary>
			/// <remarks>Ignores segments outside the valid range. A segment is accepted only after the same block value
			/// is received twice consecutively for that segment. When all segments are received, the assembled PS text is
			/// cleaned, compared with the current value, and the update event is raised only if the name changed.</remarks>
			/// <param name="blockD">16-bit RDS block D containing the two characters for the addressed PS segment.</param>
			/// <param name="segment">Zero-based PS segment index to decode; valid values are 0 through 3.</param>
			private void DecodePs( ushort blockD, int segment )
			{
				if( segment > 3 )
					return;

				if( !psLastWordValid[ segment ] || psLastWords[ segment ] != blockD )
				{
					psLastWords[ segment ] = blockD;
					psLastWordValid[ segment ] = true;
					return;
				}

				char c1 = SanitizeRdsChar( (char)( blockD >> 8 ) );
				char c2 = SanitizeRdsChar( (char)( blockD & 0xFF ) );

				psBuffer[ segment * 2 ] = c1;
				psBuffer[ segment * 2 + 1 ] = c2;
				psReceived[ segment ] = true;

				if( AllSegments( psReceived ) )
				{
					string decodedPs = CleanPs( new string( psBuffer ) );

					if( ProgramServiceName != decodedPs )
					{
						ProgramServiceName = decodedPs;
						PsUpdated?.Invoke( ProgramServiceName );
					}
				}
			}

			/// <summary>
			/// Decodes an RDS RadioText segment from received group blocks, updates the RadioText buffer when matching data is
			/// received consecutively, and triggers publication when a segment is accepted.
			/// </summary>
			/// <remarks>Segments greater than 15 are ignored. A segment is written to the buffer only after the same
			/// payload is received twice in succession for the segment, which helps reject transient errors before marking the
			/// segment as received.</remarks>
			/// <param name="blockC">16-bit RDS block C value used for RadioText version A decoding.</param>
			/// <param name="blockD">16-bit RDS block D value containing RadioText characters.</param>
			/// <param name="segment">Zero-based RadioText segment index to decode.</param>
			/// <param name="versionA">Indicates whether the group uses RadioText version A (4 characters per segment) or version B (2 characters per
			/// segment).</param>
			private void DecodeRt( ushort blockC, ushort blockD, int segment, bool versionA )
			{
				if( segment > 15 )
					return;

				if( versionA )
				{
					if( !rtLastWordValid[ segment ] || rtLastBlockC[ segment ] != blockC || rtLastBlockD[ segment ] != blockD )
					{
						rtLastBlockC[ segment ] = blockC;
						rtLastBlockD[ segment ] = blockD;
						rtLastWordValid[ segment ] = true;
						return;
					}

					int offset = segment * 4;
					rtBuffer[ offset ] = ( char )( blockC >> 8 );
					rtBuffer[ offset + 1 ] = ( char )( blockC & 0xFF );
					rtBuffer[ offset + 2 ] = ( char )( blockD >> 8 );
					rtBuffer[ offset + 3 ] = ( char )( blockD & 0xFF );
				}
				else
				{
					if( !rtLastWordValid[ segment ] || rtLastBlockD[ segment ] != blockD )
					{
						rtLastBlockD[ segment ] = blockD;
						rtLastWordValid[ segment ] = true;
						return;
					}

					int offset = segment * 2;
					rtBuffer[ offset ] = ( char )( blockD >> 8 );
					rtBuffer[ offset + 1 ] = ( char )( blockD & 0xFF );
				}

				rtReceived[ segment ] = true;
				TryPublishRt( versionA );
			}

			/// <summary>
			/// Ensures the current RadioText message context matches the supplied version and A/B flag, and reinitializes
			/// RadioText state when the context changes or has not been established.
			/// </summary>
			/// <remarks>When the stored context is uninitialized or differs from the provided values, the context is
			/// updated and RadioText parsing state is reset.</remarks>
			/// <param name="versionA">Indicates whether the RadioText payload is using version A.</param>
			/// <param name="textAbFlag">Indicates the current RadioText A/B flag used to detect message changes.</param>
			private void EnsureRtMessageContext( bool versionA, bool textAbFlag )
			{
				if( !rtTextAbValid || rtTextAbFlag != textAbFlag || rtVersionA != versionA )
				{
					rtTextAbValid = true;
					rtTextAbFlag = textAbFlag;
					rtVersionA = versionA;
					ResetRtState();
				}
			}

			/// <summary>
			/// Publishes decoded RadioText when a complete message is available and the content has changed.
			/// </summary>
			/// <remarks>Skips publishing when segments are incomplete and no terminator is present, when the decoded
			/// text is empty, or when it matches the current value. On update, sets <c>RadioText</c> and raises <c>RtUpdated</c>
			/// with the new text.</remarks>
			/// <param name="versionA">Indicates whether to decode RadioText as version A (full buffer) or version B (first 32 characters).</param>
			private void TryPublishRt( bool versionA )
			{
				if( !AllSegments( rtReceived ) && !ContainsRtTerminator( versionA ) )
				{
					return;
				}

				string decodedRt = versionA ? CleanRt( new string( rtBuffer ) ) : CleanRt( new string( rtBuffer, 0, 32 ) );

				if( decodedRt.Length == 0 || RadioText == decodedRt )
				{
					return;
				}

				RadioText = decodedRt;
				RtUpdated?.Invoke( RadioText );
			}

			/// <summary>
			/// Determines whether the RadioText buffer contains a terminator character within the active length.
			/// </summary>
			/// <remarks>Scans only the portion of the buffer defined by the selected RadioText version.</remarks>
			/// <param name="versionA">true to evaluate 64 characters for version A; false to evaluate 32 characters for version B.</param>
			/// <returns>true if a carriage return ('\r') or null character ('\0') is found within the evaluated range; otherwise, false.</returns>
			private bool ContainsRtTerminator( bool versionA )
			{
				int length = versionA ? 64 : 32;

				for( int i = 0; i < length; i++ )
				{
					if( rtBuffer[ i ] == '\r' || rtBuffer[ i ] == '\0' )
					{
						return true;
					}
				}

				return false;
			}

			/// <summary>
			/// Decodes an FM alternative frequency code, adds the mapped frequency to the internal list when valid and not
			/// already present, and raises an update notification.
			/// </summary>
			/// <remarks>Codes outside 1 to 204 are ignored. Valid codes are mapped to MHz using 87.5 + (code - 1) *
			/// 0.1.</remarks>
			/// <param name="blockC">Alternative frequency code from RDS block C in the range 1 to 204.</param>
			private void DecodeAf( ushort blockC )
			{
				if( blockC < 1 || blockC > 204 )
					return;

				double freq = 87.5 + ( blockC - 1 ) * 0.1;

				if( !afFrequencies.Contains( freq ) )
				{
					afFrequencies.Add( freq );
					AfUpdated?.Invoke( new List<double>( afFrequencies ) );
				}
			}

			/// <summary>
			/// Resets the RDS decoder state by clearing Program Service buffers, RadioText state, and cached decoded values.
			/// </summary>
			/// <remarks>Also clears the current Program Service name and RadioText values.</remarks>
			/// <param name="clearAlternativeFrequencies">Clears the stored alternative frequencies when set to <see langword="true"/>.</param>
			private void ResetDecoderState( bool clearAlternativeFrequencies )
			{
				Array.Clear( psBuffer, 0, psBuffer.Length );
				Array.Clear( psReceived, 0, psReceived.Length );
				Array.Clear( psLastWords, 0, psLastWords.Length );
				Array.Clear( psLastWordValid, 0, psLastWordValid.Length );
				ResetRtState();

				ProgramServiceName = null;
				RadioText = null;

				if( clearAlternativeFrequencies )
				{
					afFrequencies.Clear();
				}
			}
			
			/// <summary>
			/// Resets the RadioText state by clearing buffers and cached decoded values.
			/// </summary>
			private void ResetRtState()
			{
				Array.Clear( rtBuffer, 0, rtBuffer.Length );
				Array.Clear( rtReceived, 0, rtReceived.Length );
				Array.Clear( rtLastBlockC, 0, rtLastBlockC.Length );
				Array.Clear( rtLastBlockD, 0, rtLastBlockD.Length );
				Array.Clear( rtLastWordValid, 0, rtLastWordValid.Length );
			}

			/// <summary>
			/// Determines whether all elements in the specified Boolean array are `true`.
			/// </summary>
			/// <remarks>Returns `true` when `flags` is empty.</remarks>
			/// <param name="flags">The Boolean array to evaluate.</param>
			/// <returns>`true` if every element in `flags` is `true`; otherwise, `false`.</returns>
			private static bool AllSegments( bool[] flags )
			{
				for( int i = 0; i < flags.Length; i++ )
					if( !flags[ i ] )
						return false;

				return true;
			}

			/// <summary>
			/// Normalizes RDS Program Service text and removes trailing whitespace.
			/// </summary>
			/// <param name="text">Program Service text to clean.</param>
			/// <returns>Normalized Program Service text with trailing whitespace removed.</returns>
			private static string CleanPs( string text )
			{
				return NormalizeRdsText( text ).TrimEnd();
			}

			/// <summary>
			/// Cleans RT text by truncating at the first carriage return or null terminator, then normalizing and trimming it.
			/// </summary>
			/// <remarks>Input is expected to be non-null.</remarks>
			/// <param name="text">Raw RT text to sanitize.</param>
			/// <returns>Normalized RT text with protocol terminators and leading or trailing whitespace removed.</returns>
			private static string CleanRt( string text )
			{
				int endIndex = text.Length;
				int carriageReturnIndex = text.IndexOf( '\r' );
				if( carriageReturnIndex >= 0 && carriageReturnIndex < endIndex )
					endIndex = carriageReturnIndex;

				int zeroIndex = text.IndexOf( '\0' );
				if( zeroIndex >= 0 && zeroIndex < endIndex )
					endIndex = zeroIndex;

				if( endIndex < text.Length )
					text = text.Substring( 0, endIndex );

				return NormalizeRdsText( text ).Trim();
			}

			/// <summary>
			/// Normalizes RDS text by sanitizing each character.
			/// </summary>
			/// <param name="text">Text to normalize.</param>
			/// <returns>A new string containing the sanitized characters from the input text.</returns>
			private static string NormalizeRdsText( string text )
			{
				char[] chars = text.ToCharArray();

				for( int i = 0; i < chars.Length; i++ )
				{
					chars[ i ] = SanitizeRdsChar( chars[ i ] );
				}

				return new string( chars );
			}

			/// <summary>
			/// Sanitizes an RDS character by replacing null, control, and non-ASCII printable characters with a space.
			/// </summary>
			/// <param name="c">The character to sanitize.</param>
			/// <returns>A space character if the input is null, below 0x20, or above 0x7E; otherwise, the original character.</returns>
			private static char SanitizeRdsChar( char c )
			{
				if( c == '\0' )
					return ' ';

				if( c < 0x20 || c > 0x7E )
					return ' ';

				return c;
			}

			/// <summary>
			/// Converts an RDS Program Type (PTY) code to its display text.
			/// </summary>
			/// <param name="pty">Program Type code to translate.</param>
			/// <returns>The PTY display text for values 0 through 29; otherwise, "Unknown".</returns>
			private static string GetPtyText( int pty )
			{
				switch( pty )
				{
					case 0: return "None";
					case 1: return "News";
					case 2: return "Current Affairs";
					case 3: return "Information";
					case 4: return "Sport";
					case 5: return "Education";
					case 6: return "Drama";
					case 7: return "Culture";
					case 8: return "Science";
					case 9: return "Varied";
					case 10: return "Pop Music";
					case 11: return "Rock Music";
					case 12: return "Easy Listening";
					case 13: return "Light Classical";
					case 14: return "Serious Classical";
					case 15: return "Other Music";
					case 16: return "Weather";
					case 17: return "Finance";
					case 18: return "Children's";
					case 19: return "Social Affairs";
					case 20: return "Religion";
					case 21: return "Phone-In";
					case 22: return "Travel";
					case 23: return "Leisure";
					case 24: return "Jazz";
					case 25: return "Country";
					case 26: return "National Music";
					case 27: return "Oldies";
					case 28: return "Folk";
					case 29: return "Documentary";
					default: return "Unknown";
				}
			}
			
			/// <summary>
			/// Gets the region name for a given Program Identification (PI) code.
			/// </summary>
			/// <param name="pi">The PI code to decode.</param>
			/// <returns>A string representing the country and area code.</returns>
			private static string GetPiRegion( ushort pi )
			{
				int countryCode = ( pi >> 12 ) & 0xF;
				int areaCode = ( pi >> 8 ) & 0xF;

				string country = countryCode switch
				{
					0xD => "Germany",
					0xC => "France",
					0xB => "UK",
					_ => "Unknown"
				};

				return $"{country} Area {areaCode:X}";
			}
		}
	}
}
