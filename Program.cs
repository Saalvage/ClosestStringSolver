using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

string ChangeAtIthDifference(string a, string b, int i)
    => string.Join("", a.Zip(b).Select(x => x.First == x.Second || i-- != 0 ? x.First : x.Second));

string? Solve(string[] strs, int k) {
    ConcurrentDictionary<string, object?> triedCandidates = [];
    ConcurrentBag<(string Candidate, int PossibleMutations)> candidates = [(strs[0], k)];

    var sleepLock = new object();
    var threads = 32;
    var sleepers = 0;
    var over = false;
    string? result = null;
    var barrier = new Barrier(threads + 1);
    var reset = new ManualResetEventSlim(true);
    foreach (var i in Enumerable.Range(0, threads)
        .Select(_ => new Thread(() => {
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
                    if (Interlocked.Increment(ref sleepers) == threads) {
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
        }))) { i.Start(); }

    barrier.SignalAndWait();
    return result;

    bool TestCandidate(string candidate, string[] strs, int k, int possibleMutations) {
        if (triedCandidates.ContainsKey(candidate)) {
            return false;
        }
        triedCandidates.TryAdd(candidate, null);

        string? maxDiff = null;
        var d = 0;
        var len = strs[0].Length;
        foreach (var s in strs) {
            var localD = 0;
            for (var j = 0; j < len; j++) {
                if (candidate[j] != s[j]) {
                    localD++;
                }
            }
            if (localD > d) {
                d = localD;
                maxDiff = s;
            }
        }

        if (d <= k) {
            return true;
        }

        if (d > 2 * possibleMutations || possibleMutations == 0) {
            return false;
        }

        foreach (var newCandidate in Enumerable.Range(0, d)
                     .Select(i => (ChangeAtIthDifference(candidate, maxDiff, i), possibleMutations - 1))) {
            candidates.Add(newCandidate);
        }

        return false;
    }
}

string GetInput() {
    Console.Write("Please enter file path: ");
    return Console.ReadLine()!;
}

var file = args.ElementAtOrDefault(1) ?? GetInput();
await using var stream = File.OpenRead(file);
using var reader = new StreamReader(stream);

var strCount = int.Parse(await reader.ReadLineAsync());
var strs = new string[strCount];

for (var i = 0; i < strCount; i++) {
    strs[i] = await reader.ReadLineAsync();
}

var k = 0;
string? str = null;
var time = DateTimeOffset.UtcNow;

do {
    k++;
    Console.WriteLine($"Trying {k}");
} while ((str = Solve(strs, k)) == null);

Console.WriteLine($"{k} {str} {DateTimeOffset.UtcNow - time:g}");
Console.ReadLine();
