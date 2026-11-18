using System.Runtime.Intrinsics;

namespace NarrowTest;

/// <summary>One case's outcome: how many of its values differ from the scalar (truncating) reference, and where the first difference is.</summary>
public sealed record CaseResult(string Name, string Kind, long Wrong, long Total, int FirstWrongRound, string FirstWrong, double Milliseconds);

/// <summary>
/// Vector128.Narrow must truncate (keep the low bits). Every case here narrows values that do not fit the narrow type and compares the result with plain
/// integer casts. Plain .NET: no library besides the base class library. Each hot case is one method with its loop inside, so the runtime sees one hot loop.
/// </summary>
public static class NarrowCases
{
    public const int Rounds = 100_000;

    // values that use all 32 bits, different every round
    private static void Fill(uint[] input, int round)
    {
        for (int i = 0; i < input.Length; i++) input[i] = 2567483615u * (uint)(i + 1 + round) + 12345u;
    }

    private static CaseResult Done(string name, string kind, long wrong, long total, int firstRound, string first, long startTicks)
        => new(name, kind, wrong, total, firstRound, first, (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>Called ONCE, before anything is hot: the three narrowings on fixed values.</summary>
    public static CaseResult ColdSingleCall()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var input = new uint[16]; Fill(input, 7);
        var w0 = Vector128.Create(input, 0); var w1 = Vector128.Create(input, 4); var w2 = Vector128.Create(input, 8); var w3 = Vector128.Create(input, 12);
        var bytes = new byte[16];
        Vector128.Narrow(Vector128.Narrow(w0, w1), Vector128.Narrow(w2, w3)).CopyTo(bytes);
        long wrong = 0; string first = "";
        for (int i = 0; i < 16; i++) if (bytes[i] != (byte)input[i]) { if (wrong++ == 0) first = $"word {input[i]:X8}: got {bytes[i]:X2}, expected {(byte)input[i]:X2}"; }
        return Done("One call, not hot: 32 -> 16 -> 8 bits", "control", wrong, 16, wrong > 0 ? 0 : -1, first, t0);
    }

    /// <summary>The smallest failing form we know: four vectors of four 32-bit words narrowed to 16 bytes, in a loop.</summary>
    public static CaseResult Hot32To8()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var input = new uint[16]; var bytes = new byte[16];
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(input, round);
            var w0 = Vector128.Create(input, 0); var w1 = Vector128.Create(input, 4); var w2 = Vector128.Create(input, 8); var w3 = Vector128.Create(input, 12);
            Vector128.Narrow(Vector128.Narrow(w0, w1), Vector128.Narrow(w2, w3)).CopyTo(bytes);
            for (int i = 0; i < 16; i++)
                if (bytes[i] != (byte)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X8}: got {bytes[i]:X2}, expected {(byte)input[i]:X2}"; } }
        }
        return Done("Hot loop: Vector128.Narrow 32 -> 16 -> 8 bits", "test", wrong, (long)Rounds * 16, firstRound, first, t0);
    }

    /// <summary>Only the first step: 32-bit words to 16-bit.</summary>
    public static CaseResult Hot32To16()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var input = new uint[8]; var halves = new ushort[8];
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(input, round);
            Vector128.Narrow(Vector128.Create(input, 0), Vector128.Create(input, 4)).CopyTo(halves);
            for (int i = 0; i < 8; i++)
                if (halves[i] != (ushort)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X8}: got {halves[i]:X4}, expected {(ushort)input[i]:X4}"; } }
        }
        return Done("Hot loop: Vector128.Narrow 32 -> 16 bits only", "test", wrong, (long)Rounds * 8, firstRound, first, t0);
    }

    /// <summary>Only the second step: 16-bit words to bytes.</summary>
    public static CaseResult Hot16To8()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var words = new uint[16]; var input = new ushort[16]; var bytes = new byte[16];
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(words, round);
            for (int i = 0; i < 16; i++) input[i] = (ushort)(words[i] >> 8);
            Vector128.Narrow(Vector128.Create(input, 0), Vector128.Create(input, 8)).CopyTo(bytes);
            for (int i = 0; i < 16; i++)
                if (bytes[i] != (byte)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X4}: got {bytes[i]:X2}, expected {(byte)input[i]:X2}"; } }
        }
        return Done("Hot loop: Vector128.Narrow 16 -> 8 bits only", "test", wrong, (long)Rounds * 16, firstRound, first, t0);
    }

    /// <summary>Signed: 32-bit ints to 16-bit shorts.</summary>
    public static CaseResult HotSigned32To16()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var words = new uint[8]; var input = new int[8]; var halves = new short[8];
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(words, round);
            for (int i = 0; i < 8; i++) input[i] = (int)words[i];
            Vector128.Narrow(Vector128.Create(input, 0), Vector128.Create(input, 4)).CopyTo(halves);
            for (int i = 0; i < 8; i++)
                if (halves[i] != (short)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X8}: got {halves[i]:X4}, expected {(short)input[i]:X4}"; } }
        }
        return Done("Hot loop: Vector128.Narrow 32 -> 16 bits, signed", "test", wrong, (long)Rounds * 8, firstRound, first, t0);
    }

    /// <summary>Signed: 16-bit shorts to signed bytes.</summary>
    public static CaseResult HotSigned16To8()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var words = new uint[16]; var input = new short[16]; var bytes = new sbyte[16];
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(words, round);
            for (int i = 0; i < 16; i++) input[i] = (short)(words[i] >> 8);
            Vector128.Narrow(Vector128.Create(input, 0), Vector128.Create(input, 8)).CopyTo(bytes);
            for (int i = 0; i < 16; i++)
                if (bytes[i] != (sbyte)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X4}: got {bytes[i]:X2}, expected {(sbyte)input[i]:X2}"; } }
        }
        return Done("Hot loop: Vector128.Narrow 16 -> 8 bits, signed", "test", wrong, (long)Rounds * 16, firstRound, first, t0);
    }

    /// <summary>64-bit words to 32-bit.</summary>
    public static CaseResult Hot64To32()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var words = new uint[8]; var input = new ulong[4]; var halves = new uint[4];
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(words, round);
            for (int i = 0; i < 4; i++) input[i] = ((ulong)words[2 * i] << 32) | words[2 * i + 1];
            Vector128.Narrow(Vector128.Create(input, 0), Vector128.Create(input, 2)).CopyTo(halves);
            for (int i = 0; i < 4; i++)
                if (halves[i] != (uint)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X16}: got {halves[i]:X8}, expected {(uint)input[i]:X8}"; } }
        }
        return Done("Hot loop: Vector128.Narrow 64 -> 32 bits", "test", wrong, (long)Rounds * 4, firstRound, first, t0);
    }

    /// <summary>The form the bug was found in: two 256-bit vectors, mixed with shifts and xors, each narrowed half by half.</summary>
    public static CaseResult HotAsFound()
    {
        static uint Mix(uint v) { v ^= v >> 11; v ^= (v << 7) & 0x9D2C5680u; v ^= (v << 15) & 0xEFC60000u; v ^= v >> 18; return v; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var input = new uint[16]; var bytes = new byte[16];
        var mb = Vector256.Create(0x9D2C5680u); var mc = Vector256.Create(0xEFC60000u);
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(input, round);
            var m0 = Vector256.Create(input, 0); var m1 = Vector256.Create(input, 8);
            m0 ^= m0 >>> 11; m0 ^= (m0 << 7) & mb; m0 ^= (m0 << 15) & mc; m0 ^= m0 >>> 18;
            m1 ^= m1 >>> 11; m1 ^= (m1 << 7) & mb; m1 ^= (m1 << 15) & mc; m1 ^= m1 >>> 18;
            Vector128.Narrow(Vector128.Narrow(m0.GetLower(), m0.GetUpper()), Vector128.Narrow(m1.GetLower(), m1.GetUpper())).CopyTo(bytes);
            for (int i = 0; i < 16; i++)
            {
                byte want = (byte)Mix(input[i]);
                if (bytes[i] != want) { if (wrong++ == 0) { firstRound = round; first = $"mixed word {Mix(input[i]):X8}: got {bytes[i]:X2}, expected {want:X2}"; } }
            }
        }
        return Done("Hot loop: as found (256-bit vectors, shift / xor mix, then narrow)", "test", wrong, (long)Rounds * 16, firstRound, first, t0);
    }

    /// <summary>Control: the same hot loop with every word masked to 0..255 BEFORE the narrowing. Truncation and saturation then agree.</summary>
    public static CaseResult HotMaskedFirst()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var input = new uint[16]; var bytes = new byte[16];
        var low8 = Vector128.Create(0xFFu);
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(input, round);
            var w0 = Vector128.Create(input, 0) & low8; var w1 = Vector128.Create(input, 4) & low8; var w2 = Vector128.Create(input, 8) & low8; var w3 = Vector128.Create(input, 12) & low8;
            Vector128.Narrow(Vector128.Narrow(w0, w1), Vector128.Narrow(w2, w3)).CopyTo(bytes);
            for (int i = 0; i < 16; i++)
                if (bytes[i] != (byte)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X8}: got {bytes[i]:X2}, expected {(byte)input[i]:X2}"; } }
        }
        return Done("Hot loop: masked to 0..255 before narrowing", "control", wrong, (long)Rounds * 16, firstRound, first, t0);
    }

    /// <summary>Control: values that already fit the narrow type (0..255), no mask: nothing to truncate, nothing to saturate.</summary>
    public static CaseResult HotSmallValues()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var words = new uint[16]; var input = new uint[16]; var bytes = new byte[16];
        long wrong = 0; int firstRound = -1; string first = "";
        for (int round = 0; round < Rounds; round++)
        {
            Fill(words, round);
            for (int i = 0; i < 16; i++) input[i] = words[i] >> 24;
            var w0 = Vector128.Create(input, 0); var w1 = Vector128.Create(input, 4); var w2 = Vector128.Create(input, 8); var w3 = Vector128.Create(input, 12);
            Vector128.Narrow(Vector128.Narrow(w0, w1), Vector128.Narrow(w2, w3)).CopyTo(bytes);
            for (int i = 0; i < 16; i++)
                if (bytes[i] != (byte)input[i]) { if (wrong++ == 0) { firstRound = round; first = $"word {input[i]:X8}: got {bytes[i]:X2}, expected {(byte)input[i]:X2}"; } }
        }
        return Done("Hot loop: values that already fit (0..255)", "control", wrong, (long)Rounds * 16, firstRound, first, t0);
    }

    /// <summary>The cases in the order they run. The cold call must be first.</summary>
    public static readonly (string Name, Func<CaseResult> Run)[] All =
    {
        ("cold", ColdSingleCall),
        ("hot 32 -> 8", Hot32To8),
        ("hot 32 -> 16", Hot32To16),
        ("hot 16 -> 8", Hot16To8),
        ("hot signed 32 -> 16", HotSigned32To16),
        ("hot signed 16 -> 8", HotSigned16To8),
        ("hot 64 -> 32", Hot64To32),
        ("hot as found", HotAsFound),
        ("hot masked", HotMaskedFirst),
        ("hot small values", HotSmallValues),
    };
}
