using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Ats.Application.Seed;

/// <summary>
/// CPython's <c>random.Random</c> (Mersenne Twister MT19937) seeded with a string, so the demo data is the same as the Python
/// backend's: <c>seed(str)</c> (version 2: the text followed by its SHA-512, read as a big-endian integer),
/// <c>random()</c>, <c>gauss()</c> and <c>choice()</c>. Not for anything security related.
/// </summary>
public sealed class PyRandom
{
    private const int N = 624;
    private const int M = 397;
    private readonly uint[] _mt = new uint[N];
    private int _mti = N + 1;
    private double? _gaussNext;

    public PyRandom(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var bytes = Encoding.UTF8.GetBytes(seed);
        var value = new BigInteger([.. bytes, .. SHA512.HashData(bytes)], isUnsigned: true, isBigEndian: true);
        var little = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var words = Math.Max(1, (little.Length + 3) / 4);
        var key = new uint[words];
        for (var i = 0; i < little.Length; i++)
        {
            key[i / 4] |= (uint)little[i] << (8 * (i % 4));
        }

        // CPython uses only as many words as the number needs (no leading zero words).
        var used = key.Length;
        while (used > 1 && key[used - 1] == 0)
        {
            used--;
        }

        InitByArray(key.AsSpan(0, used));
    }

    private void InitGenrand(uint s)
    {
        _mt[0] = s;
        for (_mti = 1; _mti < N; _mti++)
        {
            _mt[_mti] = 1812433253u * (_mt[_mti - 1] ^ (_mt[_mti - 1] >> 30)) + (uint)_mti;
        }
    }

    private void InitByArray(ReadOnlySpan<uint> key)
    {
        InitGenrand(19650218u);
        int i = 1, j = 0;
        for (var k = Math.Max(N, key.Length); k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1664525u)) + key[j] + (uint)j;
            i++;
            j++;
            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }

            if (j >= key.Length)
            {
                j = 0;
            }
        }

        for (var k = N - 1; k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1566083941u)) - (uint)i;
            i++;
            if (i >= N)
            {
                _mt[0] = _mt[N - 1];
                i = 1;
            }
        }

        _mt[0] = 0x80000000u;
    }

    private uint NextUInt32()
    {
        if (_mti >= N)
        {
            int kk;
            uint y;
            for (kk = 0; kk < N - M; kk++)
            {
                y = (_mt[kk] & 0x80000000u) | (_mt[kk + 1] & 0x7fffffffu);
                _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ ((y & 1u) * 0x9908b0dfu);
            }

            for (; kk < N - 1; kk++)
            {
                y = (_mt[kk] & 0x80000000u) | (_mt[kk + 1] & 0x7fffffffu);
                _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ ((y & 1u) * 0x9908b0dfu);
            }

            y = (_mt[N - 1] & 0x80000000u) | (_mt[0] & 0x7fffffffu);
            _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ ((y & 1u) * 0x9908b0dfu);
            _mti = 0;
        }

        var v = _mt[_mti++];
        v ^= v >> 11;
        v ^= (v << 7) & 0x9d2c5680u;
        v ^= (v << 15) & 0xefc60000u;
        v ^= v >> 18;
        return v;
    }

    /// <summary><c>random()</c>: a float in [0, 1) with 53 random bits.</summary>
    public double Random()
    {
        var a = NextUInt32() >> 5;
        var b = NextUInt32() >> 6;
        return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
    }

    /// <summary><c>gauss(mu, sigma)</c> (the cached second value of each pair is used on the next call).</summary>
    public double Gauss(double mu, double sigma)
    {
        var z = _gaussNext;
        _gaussNext = null;
        if (z is null)
        {
            var x2pi = Random() * (2 * Math.PI);
            var g2rad = Math.Sqrt(-2.0 * Math.Log(1.0 - Random()));
            z = Math.Cos(x2pi) * g2rad;
            _gaussNext = Math.Sin(x2pi) * g2rad;
        }

        return mu + z.Value * sigma;
    }

    /// <summary><c>choice(seq)</c>, through <c>_randbelow</c> (rejection sampling on <c>getrandbits</c>).</summary>
    public T Choice<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var n = (uint)items.Count;
        var k = 32 - BitOperations.LeadingZeroCount(n);
        var r = NextUInt32() >> (32 - k);
        while (r >= n)
        {
            r = NextUInt32() >> (32 - k);
        }

        return items[(int)r];
    }
}
