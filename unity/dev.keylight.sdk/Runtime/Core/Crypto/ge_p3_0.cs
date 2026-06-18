// Vendored from Chaos.NaCl (public domain): https://github.com/CodesInChaos/Chaos.NaCl
#nullable disable

using System;

namespace Keylight.Crypto
{
	internal static partial class GroupOperations
	{
		public static void ge_p3_0(out GroupElementP3 h)
		{
			FieldOperations.fe_0(out h.X);
			FieldOperations.fe_1(out h.Y);
			FieldOperations.fe_1(out h.Z);
			FieldOperations.fe_0(out  h.T);
		}
	}
}