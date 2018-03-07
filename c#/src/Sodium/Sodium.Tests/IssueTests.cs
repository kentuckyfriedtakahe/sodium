﻿using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sodium.Tests
{
    [TestFixture]
    public class IssueTests
    {
        [Test]
        public void Issue151_PoolDoubleSubtraction_Broken()
        {
            Exception actual = null;
            try
            {
                CellSink<int> threshold = new CellSink<int>(10);
                StreamSink<int> addPoolSink = new StreamSink<int>();

                Transaction.Run(() =>
                {
                    StreamLoop<int> submitPooledAmount = new StreamLoop<int>();

                    // Ways that the pool is modified.
                    Stream<Func<int, int>> poolAddByInput = addPoolSink.Map<Func<int, int>>(i => x => x + i);
                    Stream<Func<int, int>> poolRemoveByUsage = submitPooledAmount.Map<Func<int, int>>(i => x => x - i);

                    // The current level of the pool
                    Cell<int> poolLocal = poolAddByInput
                        .Merge(poolRemoveByUsage, (f, g) => x => g(f(x)))
                        .Accum(0, (f, x) => f(x));

                    // The current input changes combined with the pool as a stream
                    Stream<int> inputByAdded =
                        poolAddByInput
                            .Snapshot(
                                poolLocal,
                                threshold,
                                (f, x, t) => f(x) >= t
                                    ? Maybe.Some(f(x))
                                    : Maybe.None)
                            .FilterMaybe();

                    // Simple rising edge on pool threshold satisfaction.
                    Stream<int> inputBySatisfaction =
                        poolLocal.Updates
                            .Snapshot(
                                poolLocal,
                                threshold,
                                (neu, alt, t) => neu >= t && alt < t
                                    ? Maybe.Some(neu)
                                    : Maybe.None)
                            .FilterMaybe();

                    submitPooledAmount.Loop(inputByAdded.Merge(inputBySatisfaction, Math.Max));

                    return (submitPooledAmount, poolLocal);
                });
            }
            catch (Exception e)
            {
                actual = e;
            }

            Assert.IsNotNull(actual);
            Assert.AreEqual("A dependency cycle was detected.", actual.Message);
        }

        [Test]
        public void Issue151_PoolDoubleSubtraction_Fixed()
        {
            CellSink<int> threshold = new CellSink<int>(10);
            StreamSink<int> addPoolSink = new StreamSink<int>();

            (Stream<int> input, Cell<int> pool) = Transaction.Run(() =>
            {
                StreamLoop<int> submitPooledAmount = new StreamLoop<int>();

                // Ways that the pool is modified.
                Stream<Func<int, int>> poolAddByInput = addPoolSink.Map<Func<int, int>>(i => x => x + i);
                StreamSink<Func<int, int>> poolRemoveByUsage = new StreamSink<Func<int, int>>();
                IStrongListener l =
                    submitPooledAmount.Listen(i => Transaction.Post(() => poolRemoveByUsage.Send(x => x - i)));

                // The current level of the pool
                Cell<int> poolLocal = poolAddByInput
                    .Merge(poolRemoveByUsage, (f, g) => x => g(f(x)))
                    .Accum(0, (f, x) => f(x));

                // The current input changes combined with the pool as a stream
                Stream<int> inputByAdded =
                    poolAddByInput
                        .Snapshot(
                            poolLocal,
                            threshold,
                            (f, x, t) => f(x) >= t
                                ? Maybe.Some(f(x))
                                : Maybe.None)
                        .FilterMaybe();

                // Simple rising edge on pool threshold satisfaction.
                Stream<int> inputBySatisfaction =
                    poolLocal.Updates
                        .Snapshot(
                            poolLocal,
                            threshold,
                            (neu, alt, t) => neu >= t && alt < t
                                ? Maybe.Some(neu)
                                : Maybe.None)
                        .FilterMaybe();

                submitPooledAmount.Loop(inputByAdded.Merge(inputBySatisfaction, Math.Max).AttachListener(l));

                return (submitPooledAmount, poolLocal);
            });

            List<int> submissions = new List<int>();
            using (input.Listen(submissions.Add))
            {
                // Add amount which can be immediately used based on threshold.
                // Pool should remain zero after the transaction is complete.
                addPoolSink.Send(10);
            }

            Assert.AreEqual(1, submissions.Count);
            Assert.AreEqual(10, submissions[0]);
            Assert.AreEqual(0, pool.Sample());
        }
    }
}
