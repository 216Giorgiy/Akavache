﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using Akavache.Deprecated;
using Akavache.Sqlite3;
using Microsoft.Reactive.Testing;
using ReactiveUI;
using ReactiveUI.Testing;
using Xunit;

using EncryptedBlobCache = Akavache.Deprecated.EncryptedBlobCache;

namespace Akavache.Tests
{
    public abstract class BlobCacheInterfaceFixture
    {
        protected abstract IBlobCache CreateBlobCache(string path);

        [Fact]
        public void CacheShouldBeAbleToGetAndInsertBlobs()
        {
            string path;
            using (Utility.WithEmptyDirectory(out path))
            using (TestUtils.WithScheduler(Scheduler.CurrentThread))
            using (var fixture = CreateBlobCache(path)) 
            {
                fixture.Insert("Foo", new byte[] { 1, 2, 3 });
                fixture.Insert("Bar", new byte[] { 4, 5, 6 });

                Assert.Throws<ArgumentNullException>(() =>
                    fixture.Insert(null, new byte[] { 7, 8, 9 }).First());

                byte[] output1 = fixture.Get("Foo").First();
                byte[] output2 = fixture.Get("Bar").First();

                Assert.Throws<ArgumentNullException>(() =>
                    fixture.Get(null).First());

                Assert.Throws<KeyNotFoundException>(() =>
                    fixture.Get("Baz").First());

                Assert.Equal(3, output1.Length);
                Assert.Equal(3, output2.Length);

                Assert.Equal(1, output1[0]);
                Assert.Equal(4, output2[0]);
            }
        }

        [Fact]
        public void CacheShouldBeRoundtrippable()
        {
            string path;

            using (Utility.WithEmptyDirectory(out path))
            {
                var fixture = CreateBlobCache(path);
                using (fixture)
                {
                    // InMemoryBlobCache isn't round-trippable by design
                    if (fixture is InMemoryBlobCache) return;

                    fixture.Insert("Foo", new byte[] {1, 2, 3});
                }

                fixture.Shutdown.Wait();

                using (var fixture2 = CreateBlobCache(path))
                {
                    var output = fixture2.Get("Foo").First();
                    Assert.Equal(3, output.Length);
                    Assert.Equal(1, output[0]);
                }
            }
        }

        [Fact]
        public void InsertingAnItemTwiceShouldAlwaysGetTheNewOne()
        {
            string path;

            using (Utility.WithEmptyDirectory(out path))
            using (var fixture = CreateBlobCache(path))
            {
                fixture.Insert("Foo", new byte[] { 1, 2, 3 }).Wait();

                var output = fixture.Get("Foo").First();
                Assert.Equal(3, output.Length);
                Assert.Equal(1, output[0]);

                fixture.Insert("Foo", new byte[] { 4, 5 }).Wait();

                output = fixture.Get("Foo").First();
                Assert.Equal(2, output.Length);
                Assert.Equal(4, output[0]);
            }
        }

        [Fact]
        public void CacheShouldRespectExpiration()
        {
            string path;

            using (Utility.WithEmptyDirectory(out path))
            {
                (new TestScheduler()).With(sched =>
                {
                    bool wasTestCache;

                    using (var fixture = CreateBlobCache(path))
                    {
                        wasTestCache = fixture is InMemoryBlobCache;
                        fixture.Insert("foo", new byte[] { 1, 2, 3 }, TimeSpan.FromMilliseconds(100));
                        fixture.Insert("bar", new byte[] { 4, 5, 6 }, TimeSpan.FromMilliseconds(500));

                        byte[] result = null;
                        sched.AdvanceToMs(20);
                        fixture.Get("foo").Subscribe(x => result = x);

                        // Foo should still be active
                        sched.AdvanceToMs(50);
                        Assert.Equal(1, result[0]);

                        // From 100 < t < 500, foo should be inactive but bar should still work
                        bool shouldFail = true;
                        sched.AdvanceToMs(120);
                        fixture.Get("foo").Subscribe(
                            x => result = x,
                            ex => shouldFail = false);
                        fixture.Get("bar").Subscribe(x => result = x);

                        sched.AdvanceToMs(300);
                        Assert.False(shouldFail);
                        Assert.Equal(4, result[0]);
                    }

                    // NB: InMemoryBlobCache is not serializable by design
                    if (wasTestCache) return;

                    sched.AdvanceToMs(350);
                    sched.AdvanceToMs(351);
                    sched.AdvanceToMs(352);

                    // Serialize out the cache and reify it again
                    using (var fixture = CreateBlobCache(path))
                    {
                        byte[] result = null;
                        fixture.Get("bar").Subscribe(x => result = x);
                        sched.AdvanceToMs(400);

                        Assert.Equal(4, result[0]);

                        // At t=1000, everything is invalidated
                        bool shouldFail = true;
                        sched.AdvanceToMs(1000);
                        fixture.Get("bar").Subscribe(
                            x => result = x,
                            ex => shouldFail = false);

                        sched.AdvanceToMs(1010);
                        Assert.False(shouldFail);
                    }

                    sched.Start();
                });
            }
        }

        [Fact]
        public void DisposedCacheThrowsObjectDisposedException()
        {
            var cache = CreateBlobCache("somepath");
            cache.Dispose();

            Assert.Throws<ObjectDisposedException>(() => cache.Insert("key", new byte[] { }).First());
        }

        [Fact]
        public void InvalidateAllReallyDoesInvalidateEverything()
        {
            string path;
            using (Utility.WithEmptyDirectory(out path)) 
            {
                using (var fixture = CreateBlobCache(path)) 
                {
                    fixture.Insert("Foo", new byte[] { 1, 2, 3 }).First();
                    fixture.Insert("Bar", new byte[] { 4, 5, 6 }).First();
                    fixture.Insert("Bamf", new byte[] { 7, 8, 9 }).First();

                    Assert.NotEqual(0, fixture.GetAllKeys().First().Count());

                    fixture.InvalidateAll().First();

                    Assert.Equal(0, fixture.GetAllKeys().First().Count());
                }

                using (var fixture = CreateBlobCache(path)) 
                {
                    Assert.Equal(0, fixture.GetAllKeys().First().Count());
                }
            }
        }

        [Fact]
        public void GetAllKeysShouldntReturnExpiredKeys()
        {
            string path;
            using (Utility.WithEmptyDirectory(out path)) 
            {
                using (var fixture = CreateBlobCache(path)) 
                {
                    var inThePast = BlobCache.TaskpoolScheduler.Now - TimeSpan.FromDays(1.0);

                    fixture.Insert("Foo", new byte[] { 1, 2, 3 }, inThePast).First();
                    fixture.Insert("Bar", new byte[] { 4, 5, 6 }, inThePast).First();
                    fixture.Insert("Bamf", new byte[] { 7, 8, 9 }).First();

                    Assert.Equal(1, fixture.GetAllKeys().First().Count());
                }

                using (var fixture = CreateBlobCache(path)) 
                {
                    if (fixture is InMemoryBlobCache) return;
                    Assert.Equal(1, fixture.GetAllKeys().First().Count());
                }
            }
        }

        [Fact]
        public void VacuumDoesntPurgeKeysThatShouldBeThere()
        {
            string path;
            using (Utility.WithEmptyDirectory(out path)) 
            {
                using (var fixture = CreateBlobCache(path)) 
                {
                    var inThePast = BlobCache.TaskpoolScheduler.Now - TimeSpan.FromDays(1.0);

                    fixture.Insert("Foo", new byte[] { 1, 2, 3 }, inThePast).First();
                    fixture.Insert("Bar", new byte[] { 4, 5, 6 }, inThePast).First();
                    fixture.Insert("Bamf", new byte[] { 7, 8, 9 }).First();

                    try 
                    {
                        fixture.Vacuum().First();
                    } 
                    catch (NotImplementedException) 
                    {
                        // NB: The old and busted cache will never have this, 
                        // just make the test pass
                    }

                    Assert.Equal(1, fixture.GetAllKeys().First().Count());
                }

                using (var fixture = CreateBlobCache(path)) 
                {
                    if (fixture is InMemoryBlobCache) return;
                    Assert.Equal(1, fixture.GetAllKeys().First().Count());
                }
            }
        }
    }

    public class TPersistentBlobCache : PersistentBlobCache
    {
        public TPersistentBlobCache(string cacheDirectory = null, IScheduler scheduler = null) : base(cacheDirectory, null, scheduler) { }
    }

    public class TEncryptedBlobCache : EncryptedBlobCache
    {
        public TEncryptedBlobCache(string cacheDirectory = null, IScheduler scheduler = null) : base(cacheDirectory, null, null, scheduler) { }
    }

    public class PersistentBlobCacheInterfaceFixture : BlobCacheInterfaceFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            return new TPersistentBlobCache(path);
        }
    }

    public class InMemoryBlobCacheInterfaceFixture : BlobCacheInterfaceFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            return new InMemoryBlobCache(RxApp.TaskpoolScheduler);
        }
    }

    public class EncryptedBlobCacheInterfaceFixture : BlobCacheInterfaceFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            return new TEncryptedBlobCache(path);
        }
    }

    public class SqliteBlobCacheInterfaceFixture : BlobCacheInterfaceFixture
    {
        protected override IBlobCache CreateBlobCache(string path)
        {
            return new SQLitePersistentBlobCache(Path.Combine(path, "sqlite.db"));
        }
    }
}