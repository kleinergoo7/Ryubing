// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.Compute;
using System;
using System.Collections.Generic;

namespace Ryujinx.Tests.Gpu
{
    public class QmdSchedulerTests
    {
        private const uint ReleaseEnable = (1u << 23) | (1u << 30);
        private const uint ReleaseAdd = ReleaseEnable | (1u << 26);

        private sealed class FakeMemory
        {
            public readonly Dictionary<ulong, int[]> Descriptors = [];
            public readonly List<(ulong Destination, uint Payload, uint Flags)> Releases = [];
            public readonly List<int[]> Dispatches = [];
            public bool ReleaseResult = true;
            public bool DispatchResult = true;

            public bool Read(ulong address, Span<int> descriptor)
            {
                if (!Descriptors.TryGetValue(address, out int[] stored))
                {
                    return false;
                }

                stored.AsSpan().CopyTo(descriptor);

                return true;
            }

            public bool Release(ulong destination, uint payload, uint flags)
            {
                Releases.Add((destination, payload, flags));

                return ReleaseResult;
            }

            public bool Dispatch(ReadOnlySpan<int> descriptor)
            {
                Dispatches.Add(descriptor.ToArray());

                return DispatchResult;
            }
        }

        private static int[] MakeDescriptor(int dependencies = 0)
        {
            int[] descriptor = new int[64];
            descriptor[4] = dependencies << 14;
            descriptor[18] = 0x30;
            descriptor[12] = 1;
            descriptor[13] = 1;
            descriptor[14] = 1;

            return descriptor;
        }

        private static void SetRelease(int[] descriptor, int slot, uint addressLow, uint payload, uint flags = ReleaseEnable)
        {
            descriptor[slot] = unchecked((int)addressLow);
            descriptor[slot + 1] = unchecked((int)flags);
            descriptor[slot + 2] = unchecked((int)payload);
        }

        private static ulong NextAddress = 0x10000000UL;

        private static ulong FreshAddress()
        {
            NextAddress += 0x100000UL;

            return NextAddress;
        }

        private static QmdScheduler Scheduler()
        {
            return new QmdScheduler();
        }

        [Test, Description("Load+schedule with zero dependencies dispatches immediately and fires releases")]
        public void Signal_LoadsSchedulesDispatchesAndReleases()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor();
            SetRelease(descriptor, 24, 0x1000u, 7u);
            memory.Descriptors[address] = descriptor;

            string error = Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch);

            Assert.That(error, Is.Null);
            Assert.That(memory.Dispatches, Has.Count.EqualTo(1));
            Assert.That(memory.Releases, Has.Count.EqualTo(1));
            Assert.That(memory.Releases[0].Destination, Is.EqualTo(0x1000UL));
            Assert.That(memory.Releases[0].Payload, Is.EqualTo(7u));
        }

        [Test, Description("Scheduled grid waits for dependencies then runs on decrement")]
        public void Signal_WaitsForDependencies()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor(dependencies: 1);
            SetRelease(descriptor, 24, 0x2000u, 9u);
            memory.Descriptors[address] = descriptor;

            QmdScheduler scheduler = Scheduler();

            Assert.That(scheduler.Signal(address, 3, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(memory.Dispatches, Is.Empty);

            Assert.That(scheduler.Signal(address, 7, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(memory.Dispatches, Has.Count.EqualTo(1));
            Assert.That(memory.Releases, Has.Count.EqualTo(1));
        }

        [Test, Description("Decrement arriving before load is deferred into the dependency count")]
        public void Signal_DefersEarlyDecrement()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor(dependencies: 1);
            memory.Descriptors[address] = descriptor;

            QmdScheduler scheduler = Scheduler();

            Assert.That(scheduler.Signal(address, 7, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(memory.Dispatches, Is.Empty);
            Assert.That(scheduler.Signal(address, 3, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(memory.Dispatches, Has.Count.EqualTo(1));
        }

        [Test, Description("Zero-size grid completes without dispatching but still releases")]
        public void Signal_ZeroSizeGridSkipsDispatch()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor();
            descriptor[12] = 0;
            SetRelease(descriptor, 24, 0x3000u, 11u);
            memory.Descriptors[address] = descriptor;

            string error = Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch);

            Assert.That(error, Is.Null);
            Assert.That(memory.Dispatches, Is.Empty);
            Assert.That(memory.Releases, Has.Count.EqualTo(1));
        }

        [Test, Description("Third release slot (word 52, patched by NVN2 for queue syncs) fires")]
        public void Signal_FiresThirdReleaseSlot()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor();
            SetRelease(descriptor, 52, 0x4000u, 13u);
            memory.Descriptors[address] = descriptor;

            string error = Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch);

            Assert.That(error, Is.Null);
            Assert.That(memory.Releases, Has.Count.EqualTo(1));
            Assert.That(memory.Releases[0].Destination, Is.EqualTo(0x4000UL));
        }

        [Test, Description("Dependent QMD chains: completion decrements the next grid in the ring")]
        public void Signal_ChainsDependentQmd()
        {
            ulong first = FreshAddress();
            ulong second = FreshAddress();
            FakeMemory memory = new();

            int[] chained = MakeDescriptor(dependencies: 1);
            memory.Descriptors[second] = chained;

            int[] head = MakeDescriptor();
            head[15] = (int)(second >> 8);
            head[16] = 1 | (4 << 1);
            memory.Descriptors[first] = head;

            QmdScheduler scheduler = Scheduler();

            Assert.That(scheduler.Signal(second, 3, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(memory.Dispatches, Is.Empty);
            Assert.That(scheduler.Signal(first, 3, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(memory.Dispatches, Has.Count.EqualTo(2));
        }

        [Test, Description("Decrement below zero reports underflow instead of running")]
        public void Signal_DependencyUnderflowErrors()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            memory.Descriptors[address] = MakeDescriptor();

            QmdScheduler scheduler = Scheduler();

            Assert.That(scheduler.Signal(address, 7, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(scheduler.Signal(address, 7, memory.Read, memory.Release, memory.Dispatch), Is.Null);

            string error = scheduler.Signal(address, 3, memory.Read, memory.Release, memory.Dispatch);

            Assert.That(error, Is.EqualTo("QMD dependency underflow"));
            Assert.That(memory.Dispatches, Is.Empty);
        }

        [Test, Description("Unknown PCAS actions are rejected, actions 0/1 ignored")]
        public void Signal_UnknownActionErrors()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            memory.Descriptors[address] = MakeDescriptor();

            Assert.That(Scheduler().Signal(address, 5, memory.Read, memory.Release, memory.Dispatch),
                Is.EqualTo("unsupported PCAS action"));
            Assert.That(Scheduler().Signal(address, 0, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(Scheduler().Signal(address, 1, memory.Read, memory.Release, memory.Dispatch), Is.Null);
            Assert.That(memory.Dispatches, Is.Empty);
        }

        [Test, Description("Rejected program still completes and fires its releases")]
        public void Signal_RejectedDispatchStillReleases()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new() { DispatchResult = false };
            int[] descriptor = MakeDescriptor();
            SetRelease(descriptor, 24, 0x7000u, 15u);
            memory.Descriptors[address] = descriptor;

            string error = Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch);

            Assert.That(error, Is.Null);
            Assert.That(memory.Dispatches, Has.Count.EqualTo(1));
            Assert.That(memory.Releases, Has.Count.EqualTo(1));
            Assert.That(memory.Releases[0].Destination, Is.EqualTo(0x7000UL));
        }

        [Test, Description("QMD release modes: plain write, add, and max-never-goes-backwards")]
        [TestCase(10u, 5u, false, false, 5u)]
        [TestCase(10u, 5u, true, false, 15u)]
        [TestCase(10u, 5u, false, true, 10u)]
        [TestCase(3u, 7u, false, true, 7u)]
        [TestCase(0u, 0u, true, false, 0u)]
        public void ApplyRelease_SelectsWriteAddOrMax(uint current, uint payload, bool add, bool max, uint expected)
        {
            Assert.That(ComputeClass.ApplyRelease(current, payload, add, max), Is.EqualTo(expected));
        }

        [Test, Description("Unmapped descriptor is reported, nothing runs")]
        public void Signal_UnmappedQmdErrors()
        {
            FakeMemory memory = new();

            Assert.That(Scheduler().Signal(FreshAddress(), 3, memory.Read, memory.Release, memory.Dispatch),
                Is.EqualTo("unmapped QMD"));
            Assert.That(memory.Dispatches, Is.Empty);
            Assert.That(memory.Releases, Is.Empty);
        }

        [Test, Description("Wrong QMD version word is rejected")]
        public void Signal_BadVersionErrors()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor();
            descriptor[18] = 0;
            memory.Descriptors[address] = descriptor;

            Assert.That(Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch),
                Is.EqualTo("unsupported QMD version or queue"));
        }

        [Test, Description("Release with forbidden flag bits is rejected before dispatch side effects")]
        public void Signal_BadReleaseFormatErrors()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor();
            SetRelease(descriptor, 24, 0x5000u, 1u, ReleaseEnable | (1u << 24));
            memory.Descriptors[address] = descriptor;

            Assert.That(Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch),
                Is.EqualTo("unsupported QMD release format"));
            Assert.That(memory.Dispatches, Is.Empty);
            Assert.That(memory.Releases, Is.Empty);
        }

        [Test, Description("Failed release write is reported as unmapped release")]
        public void Signal_FailedReleaseErrors()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new() { ReleaseResult = false };
            int[] descriptor = MakeDescriptor();
            SetRelease(descriptor, 24, 0x6000u, 1u);
            memory.Descriptors[address] = descriptor;

            Assert.That(Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch),
                Is.EqualTo("unmapped QMD release"));
        }

        [Test, Description("Second dependent QMD link is rejected")]
        public void Signal_SecondDependentErrors()
        {
            ulong address = FreshAddress();
            FakeMemory memory = new();
            int[] descriptor = MakeDescriptor();
            descriptor[16] = 1 | (4 << 1) | (1 << 5);
            memory.Descriptors[address] = descriptor;

            Assert.That(Scheduler().Signal(address, 3, memory.Read, memory.Release, memory.Dispatch),
                Is.EqualTo("second dependent QMD unsupported"));
        }
    }
}
