// Vendored from Chaos.NaCl (public domain): https://github.com/CodesInChaos/Chaos.NaCl
#nullable disable

using System;

namespace Keylight.Crypto
{
    internal static class InternalAssert
    {
        public static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("An assertion in Chaos.Crypto failed " + message);
        }
    }
}