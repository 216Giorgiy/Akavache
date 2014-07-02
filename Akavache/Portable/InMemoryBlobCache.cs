﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Splat;
using System.Reactive.Subjects;

namespace Akavache
{
    /// <summary>
    /// This class is an IBlobCache backed by a simple in-memory Dictionary.
    /// Use it for testing / mocking purposes
    /// </summary>
    public class InMemoryBlobCache : ISecureBlobCache
    {
        public InMemoryBlobCache() : this(null, null)
        {
        }

        public InMemoryBlobCache(IScheduler scheduler) : this(scheduler, null)
        {
        }

        public InMemoryBlobCache(IEnumerable<KeyValuePair<string, byte[]>> initialContents) : this(null, initialContents)
        {
        }

        public InMemoryBlobCache(IScheduler scheduler, IEnumerable<KeyValuePair<string, byte[]>> initialContents)
        {
            Scheduler = scheduler ?? System.Reactive.Concurrency.Scheduler.CurrentThread;
            foreach (var item in initialContents ?? Enumerable.Empty<KeyValuePair<string, byte[]>>())
            {
                cache[item.Key] = new CacheEntry(null, item.Value, Scheduler.Now, null);
            }
        }

        internal InMemoryBlobCache(Action disposer, 
            IScheduler scheduler, 
            IEnumerable<KeyValuePair<string, byte[]>> initialContents)
            : this(scheduler, initialContents)
        {
            inner = Disposable.Create(disposer);
        }

        public IScheduler Scheduler { get; protected set; }

        IServiceProvider serviceProvider;
        public IServiceProvider ServiceProvider
        {
            get { return serviceProvider; }
            set { serviceProvider = value; }
        }

        readonly AsyncSubject<Unit> shutdown = new AsyncSubject<Unit>();
        public IObservable<Unit> Shutdown { get { return shutdown; } }

        readonly IDisposable inner;
        bool disposed;
        Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>();

        public IObservable<Unit> Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = null)
        {
            if (disposed) throw new ObjectDisposedException("InMemoryBlobCache");
            lock (cache)
            {
                cache[key] = new CacheEntry(null, data, Scheduler.Now, absoluteExpiration);
            }

            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> Flush()
        {
            return Observable.Return(Unit.Default);
        }

        public IObservable<byte[]> Get(string key)
        {
            if (disposed) throw new ObjectDisposedException("InMemoryBlobCache");
            lock (cache)
            {
                CacheEntry entry;
                if (!cache.TryGetValue(key, out entry) || (entry.ExpiresAt != null && Scheduler.Now > entry.ExpiresAt.Value))
                {
                    cache.Remove(key);
                    return Observable.Throw<byte[]>(new KeyNotFoundException());
                }

                return Observable.Return(entry.Value, Scheduler);
            }
        }

        public IObservable<DateTimeOffset?> GetCreatedAt(string key)
        {
            lock (cache)
            {
                CacheEntry entry;
                if (!cache.TryGetValue(key, out entry))
                {
                    return Observable.Return<DateTimeOffset?>(null);
                }

                return Observable.Return<DateTimeOffset?>(entry.CreatedAt, Scheduler);
            }
        }

        public IObservable<List<string>> GetAllKeys()
        {
            if (disposed) throw new ObjectDisposedException("InMemoryBlobCache");
            lock (cache)
            {
                return Observable.Return(cache
                    .Where(x => x.Value.ExpiresAt == null || x.Value.ExpiresAt >= Scheduler.Now)
                    .Select(x => x.Key)
                    .ToList());
            }
        }

        public IObservable<Unit> Invalidate(string key)
        {
            if (disposed) throw new ObjectDisposedException("InMemoryBlobCache");
            lock (cache)
            {
                cache.Remove(key);
            }

            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> InvalidateAll()
        {
            if (disposed) throw new ObjectDisposedException("InMemoryBlobCache");
            lock (cache)
            {
                cache.Clear();
            }

            return Observable.Return(Unit.Default);
        }

        public IObservable<Unit> Vacuum()
        {
            if (disposed) throw new ObjectDisposedException("InMemoryBlobCache");
            lock (cache) 
            {
                var toDelete = cache.Where(x => x.Value.ExpiresAt >= Scheduler.Now).ToArray();
                foreach (var kvp in toDelete) cache.Remove(kvp.Key);
            }

            return Observable.Return(Unit.Default);
        }

        public void Dispose()
        {
            Scheduler = null;
            cache = null;
            if (inner != null)
            {
                inner.Dispose();
            }

            shutdown.OnNext(Unit.Default);
            shutdown.OnCompleted();
            disposed = true;
        }

        public static InMemoryBlobCache OverrideGlobals(IScheduler scheduler = null, params KeyValuePair<string, byte[]>[] initialContents)
        {
            var local = BlobCache.LocalMachine;
            var user = BlobCache.UserAccount;
            var sec = BlobCache.Secure;

            var resetBlobCache = new Action(() =>
            {
                BlobCache.LocalMachine = local;
                BlobCache.Secure = sec;
                BlobCache.UserAccount = user;
            });

            var testCache = new InMemoryBlobCache(resetBlobCache, scheduler, initialContents);
            BlobCache.LocalMachine = testCache;
            BlobCache.Secure = testCache;
            BlobCache.UserAccount = testCache;

            return testCache;
        }

        public static InMemoryBlobCache OverrideGlobals(IDictionary<string, byte[]> initialContents, IScheduler scheduler = null)
        {
            return OverrideGlobals(scheduler, initialContents.ToArray());
        }

        public static InMemoryBlobCache OverrideGlobals(IDictionary<string, object> initialContents, IScheduler scheduler = null)
        {
            var initialSerializedContents = initialContents
                .Select(item => new KeyValuePair<string, byte[]>(item.Key, JsonSerializationMixin.SerializeObject(item.Value)))
                .ToArray();

            return OverrideGlobals(scheduler, initialSerializedContents);
        }
    }

    public class CacheEntry
    {
        public DateTimeOffset CreatedAt { get; protected set; }
        public DateTimeOffset? ExpiresAt { get; protected set; }
        public string TypeName { get; protected set; }
        public byte[] Value { get; protected set; }

        public CacheEntry(string typeName, byte[] value, DateTimeOffset createdAt, DateTimeOffset? expiresAt)
        {
            TypeName = typeName;
            Value = value;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
        }
    }
}
