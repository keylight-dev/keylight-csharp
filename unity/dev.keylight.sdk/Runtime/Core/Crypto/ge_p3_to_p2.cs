// Vendored from Chaos.NaCl (public domain): https://github.com/CodesInChaos/Chaos.NaCl
#nullable disable

using System;

namespace Keylight.Crypto
{
	internal static partial class GroupOperations
	{
		/*
		r = p
		*/
		public static void ge_p3_to_p2(out GroupElementP2 r, ref GroupElementP3 p)
		{
			r.X = p.X;
			r.Y = p.Y;
			r.Z = p.Z;
		}
	}
}