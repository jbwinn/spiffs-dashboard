using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SalesLedger.Core.Models;

namespace SalesLedger.Core.Services
{
    public enum SyncActionType { Upsert, Delete, Rebuild }

    public class SyncAction
    {
        public SyncActionType ActionType { get; }
        public SaleRecord? Record { get; }
        public Guid RecordId { get; }

        private SyncAction(SyncActionType type, SaleRecord? record, Guid id)
        {
            ActionType = type;
            Record = record;
            RecordId = id;
        }

        public static SyncAction CreateUpsert(SaleRecord record) => new SyncAction(SyncActionType.Upsert, record, record.Id);
        public static SyncAction CreateDelete(Guid id) => new SyncAction(SyncActionType.Delete, null, id);
        public static SyncAction CreateRebuild() => new SyncAction(SyncActionType.Rebuild, null, Guid.Empty);
    }

    public class SyncPipeline
    {
        private readonly Channel<SyncAction> _channel;
        private readonly LiteDbService _liteDb;
        private readonly DuckDbService _duckDb;
        private readonly CancellationTokenSource _cts;
        private Task? _processingTask;

        public SyncPipeline(LiteDbService liteDb, DuckDbService duckDb)
        {
            _liteDb = liteDb;
            _duckDb = duckDb;
            _channel = Channel.CreateUnbounded<SyncAction>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            _processingTask = Task.Run(ProcessQueueAsync);
            // Trigger an initial rebuild to make sure DuckDB analytics are fully in sync with LiteDB on startup
            QueueRebuild();
        }

        public void QueueUpsert(SaleRecord record)
        {
            _channel.Writer.TryWrite(SyncAction.CreateUpsert(record));
        }

        public void QueueDelete(Guid id)
        {
            _channel.Writer.TryWrite(SyncAction.CreateDelete(id));
        }

        public void QueueRebuild()
        {
            _channel.Writer.TryWrite(SyncAction.CreateRebuild());
        }

        private async Task ProcessQueueAsync()
        {
            var reader = _channel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token))
                {
                    while (reader.TryRead(out var action))
                    {
                        try
                        {
                            switch (action.ActionType)
                            {
                                case SyncActionType.Upsert:
                                    if (action.Record != null)
                                    {
                                        _duckDb.UpsertSale(action.Record);
                                    }
                                    break;
                                case SyncActionType.Delete:
                                    _duckDb.DeleteSale(action.RecordId);
                                    break;
                                case SyncActionType.Rebuild:
                                    RebuildDuckDb();
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SyncPipeline] Error processing action {action.ActionType}: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        private void RebuildDuckDb()
        {
            try
            {
                _duckDb.ClearAll();
                var allSales = _liteDb.Sales.FindAll();
                foreach (var sale in allSales)
                {
                    _duckDb.UpsertSale(sale);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SyncPipeline] Rebuild error: {ex.Message}");
            }
        }

        public void Stop()
        {
            _cts.Cancel();
            _channel.Writer.Complete();
            try
            {
                _processingTask?.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Ignore errors during task join
            }
        }
    }
}
