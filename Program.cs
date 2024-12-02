using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using Standart.Hash.xxHash;

using var threadPool = new ThreadPool(Environment.ProcessorCount);

var file = args.ElementAtOrDefault(1) ?? GetInput();
await using var stream = File.OpenRead(file);
using var reader = new StreamReader(stream);

var strCount = int.Parse(await reader.ReadLineAsync());
var strs = new U8String[strCount];

for (var i = 0; i < strCount; i++) {
    var line = await reader.ReadLineAsync();
    strs[i] = new(line);
}

var k = 0;
U8String? str = null;
var time = DateTimeOffset.UtcNow;

do {
    k++;
    Console.WriteLine($"Trying {k}");
} while ((str = Solve(strs, k)) == null);

Console.WriteLine($"{k} {str} {DateTimeOffset.UtcNow - time:g}");

string GetInput() {
    Console.Write("Please enter file path: ");
    return Console.ReadLine()!;
}

U8String ChangeAtIthDifference(U8String a, U8String b, int i) {
    var ret = a.Data.ToArray();
    for (var j = 0; j < a.Length; j++) {
        if (a[j] != b[j] && i-- == 0) {
            ret[j] = b[j];
            break;
        }
    }
    return new(ret);
}

U8String? Solve(U8String[] strs, int k) {
    ConcurrentDictionary<U8String, object?> triedCandidates = [];
    ConcurrentBag<(U8String Candidate, int PossibleMutations)> candidates = [(strs[0], k)];

    var sleepLock = new object();
    var sleepers = 0;
    var over = false;
    U8String? result = null;
    var barrier = new Barrier(threadPool.Count + 1);
    var reset = new ManualResetEventSlim(true);
    threadPool.Submit(() => {
        while (!over) {
            if (candidates.TryTake(out var candidate)) {
                if (TestCandidate(candidate.Candidate, strs, k, candidate.PossibleMutations)) {
                    result = candidate.Candidate;
                    lock (sleepLock) {
                        over = true;
                        reset.Set();
                    }
                }
                reset.Set();
            } else {
                if (Interlocked.Increment(ref sleepers) == threadPool.Count) {
                    lock (sleepLock) {
                        over = true;
                        reset.Set();
                    }
                } else {
                    lock (sleepLock) {
                        if (!over) {
                            reset.Reset();
                        }
                    }
                    reset.Wait();
                    Interlocked.Decrement(ref sleepers);
                }
            }
        }

        barrier.SignalAndWait();
    });

    barrier.SignalAndWait();
    return result;

    unsafe bool TestCandidate(U8String candidate, U8String[] strs, int k, int possibleMutations) {
        if (triedCandidates.ContainsKey(candidate)) {
            return false;
        }
        triedCandidates.TryAdd(candidate, null);

        U8String maxDiff = default;
        var d = 0;
        var len = strs[0].Length;
        fixed (byte* a = candidate.Data) {
            foreach (var s in strs) {
                var localD = 0;
                fixed (byte* b = s.Data) {
                    for (var j = 0; j < len; j += U8String.BYTES_PER_VECTOR) {
                        var av = Avx2.LoadVector256(a + j);
                        var bv = Avx2.LoadVector256(b + j);
                        var cmp = Avx2.CompareEqual(av, bv);
                        localD += 32 - BitOperations.PopCount((uint)Avx2.MoveMask(cmp));
                    }
                }
                if (localD > d) {
                    d = localD;
                    maxDiff = s;
                }
            }
        }

        if (d <= k) {
            return true;
        }

        if (d > k + possibleMutations || possibleMutations == 0) {
            return false;
        }

        foreach (var newCandidate in Enumerable.Range(0, d)
                     .Select(i => (ChangeAtIthDifference(candidate, maxDiff, i), possibleMutations - 1))) {
            candidates.Add(newCandidate);
        }

        return false;
    }
}

class ThreadPool : IDisposable {
    private readonly Thread[] _threads;
    private readonly Barrier _barrier;

    private bool _over;
    private Action _action;

    public int Count => _threads.Length;

    public ThreadPool(int count) {
        _threads = new Thread[count];
        _barrier = new(count + 1);
        var tp = this;
        foreach (ref var t in _threads.AsSpan()) {
            t = new(() => {
                while (true) {
                    _barrier.SignalAndWait();
                    if (tp._over) {
                        break;
                    }
                    tp._action!();
                }
            });
            t.Start();
        }
    }

    public void Submit(Action action) {
        _action = action;
        _barrier.SignalAndWait();
    }

    public void Dispose() {
        _over = true;
        _barrier.SignalAndWait();
        _barrier.Dispose();
    }
}

readonly struct U8String : IEquatable<U8String> {
    public const int BYTES_PER_VECTOR = 256 / 8;

    private readonly byte[] _data;
    public ReadOnlySpan<byte> Data => _data;

    public int Length => _data.Length;

    public U8String(byte[] data) {
        Debug.Assert(data.Length % BYTES_PER_VECTOR == 0);
        _data = data;
    }

    public U8String(string str) : this(str.Select(x => (byte)x)
        // We pad to multiples of 256 bits to allow for SIMD without edge cases.
        .Concat(Enumerable.Repeat<byte>(0, BYTES_PER_VECTOR - str.Length % BYTES_PER_VECTOR))
        .ToArray())
    { }

    public byte this[int i] => _data[i];

    public override string ToString() => string.Join("", _data.TakeWhile(x => x != 0).Select(x => (char)x));

    public override int GetHashCode() {
        return (int)xxHash3.ComputeHash(_data, _data.Length);
    }

    public override bool Equals(object? obj) {
        return obj is U8String other && Equals(other);
    }

    public unsafe bool Equals(U8String other) {
        fixed (byte* a = &_data[0]) {
            fixed (byte* b = &other._data[0]) {
                for (var i = 0; i < _data.Length; i += BYTES_PER_VECTOR) {
                    var av = Avx2.LoadVector256(a + i);
                    var bv = Avx2.LoadVector256(b + i);
                    var cmp = Avx2.CompareEqual(av, bv);
                    // -1 <=> all bits set
                    if (Avx2.MoveMask(cmp) != -1) {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
