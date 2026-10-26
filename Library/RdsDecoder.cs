using System;
using System.Collections.Generic;

namespace ImplicateX.TinyCLR.Drivers.Tuner
{
	public partial class Tef668x
	{
		public sealed class RdsDisplayModel
		{
			public string PsName { get; set; }
			public string RadioText { get; set; }
			public string PtyText { get; set; }
			public string PiRegion { get; set; }
			public bool Tp { get; set; }
			public bool Ta { get; set; }
			public IList<double> AfList { get; set; }
		}

		public sealed class RdsDecoder
		{
			private readonly char[] psBuffer = new char[ 8 ];
			private readonly char[] rtBuffer = new char[ 64 ];
			private readonly bool[] psReceived = new bool[ 4 ];
			private readonly bool[] rtReceived = new bool[ 16 ];

			private readonly List<double> afFrequencies = new List<double>();

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

			public void ProcessFrame( Tef668x.RdsFrame frame )
			{
				if( !frame.HasData && !frame.HasDataLoss )
					return;

				ushort blockA = frame.BlockA;
				ushort blockB = frame.BlockB;
				ushort blockC = frame.BlockC;
				ushort blockD = frame.BlockD;

				ProgramIdentifier = blockA;

				int groupType = ( blockB >> 12 ) & 0xF;
				bool versionA = ( ( blockB >> 11 ) & 1 ) == 0;
				ProgramType = ( blockB >> 5 ) & 0x1F;
				TrafficProgram = ( ( blockB >> 10 ) & 1 ) != 0;
				TrafficAnnouncement = ( ( blockB >> 4 ) & 1 ) != 0;

				TrafficStatusUpdated?.Invoke( TrafficProgram, TrafficAnnouncement );

				int textIndex = blockB & 0x1F;

				switch( groupType )
				{
					case 0:
						DecodePs( blockD, textIndex );
						DecodeAf( blockC );
						break;

					case 2:
						DecodeRt( blockC, blockD, textIndex, versionA );
						break;

					default:
						break;
				}
			}

			private void DecodePs( ushort blockD, int segment )
			{
				if( segment > 3 )
					return;

				char c1 = ( char )( blockD >> 8 );
				char c2 = ( char )( blockD & 0xFF );

				psBuffer[ segment * 2 ] = c1;
				psBuffer[ segment * 2 + 1 ] = c2;
				psReceived[ segment ] = true;

				if( AllSegments( psReceived ) )
				{
					ProgramServiceName = new string( psBuffer );
					PsUpdated?.Invoke( ProgramServiceName );
				}
			}

			private void DecodeRt( ushort blockC, ushort blockD, int segment, bool versionA )
			{
				if( versionA )
				{
					if( segment > 15 )
						return;

					char c1 = ( char )( blockC >> 8 );
					char c2 = ( char )( blockC & 0xFF );
					char c3 = ( char )( blockD >> 8 );
					char c4 = ( char )( blockD & 0xFF );

					int offset = segment * 4;
					rtBuffer[ offset ] = c1;
					rtBuffer[ offset + 1 ] = c2;
					rtBuffer[ offset + 2 ] = c3;
					rtBuffer[ offset + 3 ] = c4;

					rtReceived[ segment ] = true;

					if( AllSegments( rtReceived ) )
					{
						RadioText = CleanRt( new string( rtBuffer ) );
						RtUpdated?.Invoke( RadioText );
					}
				}
				else
				{
					if( segment > 15 )
						return;

					char c1 = ( char )( blockD >> 8 );
					char c2 = ( char )( blockD & 0xFF );

					int offset = segment * 2;
					rtBuffer[ offset ] = c1;
					rtBuffer[ offset + 1 ] = c2;

					rtReceived[ segment ] = true;

					if( AllSegments( rtReceived ) )
					{
						RadioText = CleanRt( new string( rtBuffer, 0, 32 ) );
						RtUpdated?.Invoke( RadioText );
					}
				}
			}

			private void DecodeAf( ushort blockC )
			{
				if( blockC < 1 || blockC > 204 )
					return;

				double freq = 87.5 + ( blockC - 1 ) * 0.1;

				if( !afFrequencies.Contains( freq ) )
				{
					afFrequencies.Add( freq );
					AfUpdated?.Invoke( AlternativeFrequencies );
				}
			}

			private static bool AllSegments( bool[] flags )
			{
				for( int i = 0; i < flags.Length; i++ )
					if( !flags[ i ] )
						return false;

				return true;
			}

			private static string CleanRt( string text )
			{
				int zeroIndex = text.IndexOf( '\0' );
				if( zeroIndex >= 0 )
					text = text.Substring( 0, zeroIndex );

				return text.Trim();
			}

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
