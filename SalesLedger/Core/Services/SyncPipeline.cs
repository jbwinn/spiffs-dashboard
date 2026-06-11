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

        public static SyncAction CreateUpsert(SaleRecord record) => new(SyncActionType.Upsert, record, record.Id);
        public static SyncAction CreateDelete(Guid id) => new(SyncActionType.Delete, null, id);
        public static SyncAction CreateRebuild() => new(SyncActionType.Rebuild, null, Guid.Empty);
    }

    public class SyncPipeline(LiteDbService liteDb, DuckDbService duckDb)
    {
        private readonly Channel<SyncAction> _channel = Channel.CreateUnbounded<SyncAction>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private readonly CancellationTokenSource _cts = new();
        private Task? _processingTask;

        public event Action? SyncCompleted;

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
                    bool processedAny = false;
                    while (reader.TryRead(out var action))
                    {
                        try
                        {
                            switch (action.ActionType)
                            {
                                case SyncActionType.Upsert:
                                    if (action.Record != null)
                                    {
                                        duckDb.UpsertSale(action.Record);
                                    }
                                    break;
                                case SyncActionType.Delete:
                                    duckDb.DeleteSale(action.RecordId);
                                    break;
                                case SyncActionType.Rebuild:
                                    RebuildDuckDb();
                                    break;
                            }
                            processedAny = true;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SyncPipeline] Error processing action {action.ActionType}: {ex.Message}");
                        }
                    }

                    if (processedAny)
                    {
                        SyncCompleted?.Invoke();
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
                duckDb.ClearAll();
                var allSales = liteDb.Sales.FindAll();
                foreach (var sale in allSales)
                {
                    duckDb.UpsertSale(sale);
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
