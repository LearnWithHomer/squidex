﻿// ==========================================================================
//  MongoEventStore.cs
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex Group
//  All rights reserved.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using NodaTime;
using Squidex.Infrastructure.CQRS.Events;
using Squidex.Infrastructure.Reflection;

// ReSharper disable ConvertIfStatementToConditionalTernaryExpression
// ReSharper disable ClassNeverInstantiated.Local
// ReSharper disable UnusedMember.Local
// ReSharper disable InvertIf

namespace Squidex.Infrastructure.MongoDb.EventStore
{
    public class MongoEventStore : MongoRepositoryBase<MongoEventCommit>, IEventStore
    {
        private const int Retries = 500;
        private readonly IEventNotifier notifier;
        private readonly IClock clock;
        private string eventsOffsetIndex;

        public MongoEventStore(IMongoDatabase database, IEventNotifier notifier, IClock clock) 
            : base(database)
        {
            Guard.NotNull(clock, nameof(clock));
            Guard.NotNull(notifier, nameof(notifier));

            this.clock = clock;

            this.notifier = notifier;
        }

        protected override string CollectionName()
        {
            return "Events";
        }

        protected override MongoCollectionSettings CollectionSettings()
        {
            return new MongoCollectionSettings { WriteConcern = WriteConcern.WMajority };
        }

        protected override async Task SetupCollectionAsync(IMongoCollection<MongoEventCommit> collection)
        {
            var indexNames =
                await Task.WhenAll(
                    collection.Indexes.CreateOneAsync(IndexKeys.Ascending(x => x.EventsOffset), new CreateIndexOptions { Unique = true }),
                    collection.Indexes.CreateOneAsync(IndexKeys.Ascending(x => x.EventStreamOffset).Ascending(x => x.EventStream), new CreateIndexOptions { Unique = true }),
                    collection.Indexes.CreateOneAsync(IndexKeys.Descending(x => x.EventsOffset), new CreateIndexOptions { Unique = true }),
                    collection.Indexes.CreateOneAsync(IndexKeys.Descending(x => x.EventStreamOffset).Ascending(x => x.EventStream), new CreateIndexOptions { Unique = true }));

            eventsOffsetIndex = indexNames[0];
        }

        public IObservable<StoredEvent> GetEventsAsync(string streamFilter, long lastReceivedEventNumber = -1)
        {
            return Observable.Create<StoredEvent>((observer, ct) =>
            {
                return GetEventsAsync(storedEvent =>
                {
                    observer.OnNext(storedEvent);

                    return Tasks.TaskHelper.Done;
                }, ct, streamFilter, lastReceivedEventNumber);
            });
        }

        public async Task GetEventsAsync(Func<StoredEvent, Task> callback, CancellationToken cancellationToken, string streamFilter = null, long lastReceivedEventNumber = -1)
        {
            Guard.NotNull(callback, nameof(callback));
            
            var filters = new List<FilterDefinition<MongoEventCommit>>();

            if (lastReceivedEventNumber >= 0)
            {
                var commitOffset = await GetPreviousOffsetAsync(lastReceivedEventNumber);

                filters.Add(Filter.Gte(x => x.EventsOffset, commitOffset));
            }

            if (!string.IsNullOrWhiteSpace(streamFilter) && !string.Equals(streamFilter, "*", StringComparison.OrdinalIgnoreCase))
            {
                if (streamFilter.StartsWith("^"))
                {
                    filters.Add(Filter.Regex(x => x.EventStream, streamFilter));
                }
                else
                {
                    filters.Add(Filter.Eq(x => x.EventStream, streamFilter));
                }
            }

            FilterDefinition<MongoEventCommit> filter = new BsonDocument();

            if (filters.Count > 1)
            {
                filter = Filter.And(filters);
            }
            else if (filters.Count == 1)
            {
                filter = filters[0];
            }

            await Collection.Find(filter).SortBy(x => x.EventsOffset).ForEachAsync(async commit =>
            {
                var eventNumber = commit.EventsOffset;
                var eventStreamNumber = commit.EventStreamOffset;

                foreach (var mongoEvent in commit.Events)
                {
                    eventNumber++;
                    eventStreamNumber++;

                    if (eventNumber > lastReceivedEventNumber)
                    {
                        var eventData = SimpleMapper.Map(mongoEvent, new EventData());

                        await callback(new StoredEvent(eventNumber, eventStreamNumber, eventData));
                    }

                }
            }, cancellationToken);
        }

        public async Task AppendEventsAsync(Guid commitId, string streamName, int expectedVersion, IEnumerable<EventData> events)
        {
            Guard.NotNullOrEmpty(streamName, nameof(streamName));
            Guard.NotNull(events, nameof(events));

            var currentVersion = await GetEventStreamOffset(streamName);

            if (currentVersion != expectedVersion)
            {
                throw new WrongEventVersionException(currentVersion, expectedVersion);
            }

            var now = clock.GetCurrentInstant();

            var commitEvents = events.Select(x => SimpleMapper.Map(x, new MongoEvent())).ToList();

            if (commitEvents.Any())
            {
                var offset = await GetEventOffsetAsync();

                var commit = new MongoEventCommit
                {
                    Id = commitId,
                    Events = commitEvents,
                    EventsOffset = offset,
                    EventsCount = commitEvents.Count,
                    EventStream = streamName,
                    EventStreamOffset = expectedVersion,
                    Timestamp = now
                };

                for (var retry = 0; retry < Retries; retry++)
                {
                    try
                    {
                        await Collection.InsertOneAsync(commit);

                        notifier.NotifyEventsStored();

                        return;
                    }
                    catch (MongoWriteException ex)
                    {
                        if (ex.Message.IndexOf(eventsOffsetIndex, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            commit.EventsOffset = await GetEventOffsetAsync();
                        }
                        else if (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                        {
                            currentVersion = await GetEventStreamOffset(streamName);

                            throw new WrongEventVersionException(currentVersion, expectedVersion);
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }
        }

        private async Task<long> GetPreviousOffsetAsync(long startEventNumber)
        {
            var document =
                await Collection.Find(x => x.EventsOffset <= startEventNumber)
                    .Project<BsonDocument>(Projection
                        .Include(x => x.EventsOffset))
                    .SortByDescending(x => x.EventsOffset).Limit(1)
                    .FirstOrDefaultAsync();

            if (document != null)
            {
                return document["EventsOffset"].ToInt64();
            }

            return -1;
        }

        private async Task<long> GetEventOffsetAsync()
        {
            var document =
                await Collection.Find(new BsonDocument())
                    .Project<BsonDocument>(Projection
                        .Include(x => x.EventsOffset)
                        .Include(x => x.EventsCount))
                    .SortByDescending(x => x.EventsOffset).Limit(1)
                    .FirstOrDefaultAsync();

            if (document != null)
            {
                return document["EventsOffset"].ToInt64() + document["EventsCount"].ToInt64();
            }

            return -1;
        }

        private async Task<long> GetEventStreamOffset(string streamName)
        {
            var document =
                await Collection.Find(x => x.EventStream == streamName)
                    .Project<BsonDocument>(Projection
                        .Include(x => x.EventStreamOffset)
                        .Include(x => x.EventsCount))
                    .SortByDescending(x => x.EventsOffset).Limit(1)
                    .FirstOrDefaultAsync();

            if (document != null)
            {
                return document["EventStreamOffset"].ToInt64() + document["EventsCount"].ToInt64();
            }

            return -1;
        }
    }
}
