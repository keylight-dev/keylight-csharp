// Vendored from Chaos.NaCl (public domain): https://github.com/CodesInChaos/Chaos.NaCl
#nullable disable

using System;

namespace Keylight.Crypto
{
	internal static partial class FieldOperations
	{
		public static void fe_1(out FieldElement h)
		{
			h = default(FieldElement);
			h.x0 = 1;
		}
	}
}