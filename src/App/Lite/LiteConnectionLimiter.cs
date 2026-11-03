// MwbLite: limits on who may connect to the control channel (LiteControl), checked before any TLS work is done.
//
// - At most MaxPerAddress connections at once from one remote address, so one PC (or one program on the LAN) can't
//   use up the listener's 16 slots.
// - An address that fails the security-key proof is refused for RefuseFor (30 s), so guessing the key is slow and
//   costs the listener nothing but an accept and a close. The list of refused addresses is bounded and pruned.
// The clock is a parameter so the tests don't wait.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace MouseWithoutBorders.Lite
{
    internal sealed class LiteConnectionLimiter
    {
        internal const int DefaultMaxPerAddress = 2;
        internal const int MaxRefused = 256;

        internal static readonly TimeSpan DefaultRefuseFor = TimeSpan.FromSeconds(30);

        private readonly int maxPerAddress;
        private readonly TimeSpan refuseFor;
        private readonly Func<DateTime> utcNow;
        private readonly Dictionary<IPAddress, int> open = new();
        private readonly Dictionary<IPAddress, DateTime> refusedUntil = new();
        private readonly object gate = new();

        internal LiteConnectionLimiter(int maxPerAddress = DefaultMaxPerAddress, TimeSpan? refuseFor = null, Func<DateTime> utcNow = null)
        {
            this.maxPerAddress = maxPerAddress;
            this.refuseFor = refuseFor ?? DefaultRefuseFor;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>The same address however it was reported: IPv4 inside IPv6 becomes IPv4, an IPv6 scope is dropped.</summary>
        internal static IPAddress Normalize(IPAddress address)
        {
            if (address == null)
            {
                return null;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                return address.MapToIPv4();
            }

            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? new IPAddress(address.GetAddressBytes()) : address;
        }

        /// <summary>
        /// Counts a new connection from <paramref name="address"/>; false (not counted) if the address is refused or already
        /// has the most connections it may. Every true must be matched by one <see cref="Exit"/>.
        /// </summary>
        internal bool TryEnter(IPAddress address)
        {
            address = Normalize(address);
            if (address == null)
            {
                return false;
            }

            lock (gate)
            {
                if (IsRefusedLocked(address) || (open.TryGetValue(address, out int count) && count >= maxPerAddress))
                {
                    return false;
                }

                open[address] = count + 1;
                return true;
            }
        }

        internal void Exit(IPAddress address)
        {
            address = Normalize(address);
            if (address == null)
            {
                return;
            }

            lock (gate)
            {
                if (open.TryGetValue(address, out int count))
                {
                    if (count <= 1)
                    {
                        _ = open.Remove(address);
                    }
                    else
                    {
                        open[address] = count - 1;
                    }
                }
            }
        }

        /// <summary>The address failed the key proof: no connection from it for the refusal period.</summary>
        internal void RecordKeyFailure(IPAddress address)
        {
            address = Normalize(address);
            if (address == null)
            {
                return;
            }

            lock (gate)
            {
                DateTime now = utcNow();
                if (refusedUntil.Count >= MaxRefused && !refusedUntil.ContainsKey(address))
                {
                    foreach (IPAddress expired in refusedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                    {
                        _ = refusedUntil.Remove(expired);
                    }

                    if (refusedUntil.Count >= MaxRefused)
                    {
                        // Still full of live entries: forget the ones that end soonest (a flood of addresses can't grow this).
                        foreach (IPAddress soonest in refusedUntil.OrderBy(kv => kv.Value).Take(MaxRefused / 4).Select(kv => kv.Key).ToList())
                        {
                            _ = refusedUntil.Remove(soonest);
                        }
                    }
                }

                refusedUntil[address] = now + refuseFor;
            }
        }

        internal bool IsRefused(IPAddress address)
        {
            address = Normalize(address);
            lock (gate)
            {
                return address != null && IsRefusedLocked(address);
            }
        }

        /// <summary>How many addresses are on the refused list (including ones that have expired but are not pruned yet).</summary>
        internal int RefusedCount
        {
            get
            {
                lock (gate)
                {
                    return refusedUntil.Count;
                }
            }
        }

        internal int OpenCount(IPAddress address)
        {
            lock (gate)
            {
                return open.TryGetValue(Normalize(address) ?? IPAddress.None, out int count) ? count : 0;
            }
        }

        internal void Clear()
        {
            lock (gate)
            {
                open.Clear();
                refusedUntil.Clear();
            }
        }

        private bool IsRefusedLocked(IPAddress address)
        {
            if (!refusedUntil.TryGetValue(address, out DateTime until))
            {
                return false;
            }

            DateTime now = utcNow();
            if (now >= until || until - now > refuseFor)
            {
                _ = refusedUntil.Remove(address); // over, or the clock was set back by more than the whole period
                return false;
            }

            return true;
        }
    }
}
