using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FastThing
{
    // -----------------------------------------------------------------------
    // SearchEngine – wraps index and provides fast search
    // -----------------------------------------------------------------------
    public sealed class SearchEngine
    {
        // The master index – accessed from multiple threads, immutable after set
        private volatile List<FileRecord> _index = new();
        private volatile bool _indexReady = false;

        public event Action<string>? StatusChanged;
        public event Action<bool>? IndexReadyChanged; // true = ready

        public bool IsIndexReady => _indexReady;
        public int RecordCount => _index.Count;
        public DateTime IndexTime { get; private set; } = DateTime.MinValue;

        // -------------------------------------------------------------------
        // Initialize: load cached index immediately, then rebuild in background
        // -------------------------------------------------------------------
        public void Initialize()
        {
            // Step 1: load saved index (fast, allows immediate searching)
            Task.Run(() =>
            {
                try
                {
                    StatusChanged?.Invoke("正在加载已保存的索引...");
                    var cached = IndexStore.Load();
                    if (cached.Count > 0)
                    {
                        _index = cached;
                        _indexReady = true;
                        IndexTime = IndexStore.GetIndexTime();
                        StatusChanged?.Invoke($"已加载 {cached.Count:N0} 条记录（{IndexTime:yyyy-MM-dd HH:mm:ss}）");
                        IndexReadyChanged?.Invoke(true);
                    }
                }
                catch { /* ignore */ }

                // Step 2: rebuild index from MFT in background
                RebuildIndexBackground();
            });
        }

        private CancellationTokenSource _rebuildCts = new();
        // Tracks whether a save is already pending/running so we don't queue duplicates
        private int _savePending = 0;

        private void RebuildIndexBackground()
        {
            // Cancel any previous rebuild
            _rebuildCts.Cancel();
            _rebuildCts.Dispose();
            _rebuildCts = new CancellationTokenSource();
            var token = _rebuildCts.Token;

            Task.Run(() =>
            {
                try
                {
                    StatusChanged?.Invoke("正在后台更新索引...");
                    var indexer = new NtfsIndexer();
                    indexer.StatusChanged += msg => StatusChanged?.Invoke(msg);

                    var newIndex = indexer.BuildIndex(token);
                    if (token.IsCancellationRequested) return;

                    if (newIndex.Count > 0)
                    {
                        _index = newIndex;
                        _indexReady = true;
                        IndexTime = DateTime.Now;
                        StatusChanged?.Invoke($"索引已更新：{newIndex.Count:N0} 条记录");
                        IndexReadyChanged?.Invoke(true);

                        // Only queue a save if none is already pending/running
                        if (Interlocked.CompareExchange(ref _savePending, 1, 0) == 0)
                        {
                            Task.Run(() =>
                            {
                                try
                                {
                                    StatusChanged?.Invoke("正在保存索引...");
                                    IndexStore.Save(newIndex);
                                    StatusChanged?.Invoke($"索引已保存，共 {newIndex.Count:N0} 条记录");
                                }
                                catch (Exception ex)
                                {
                                    StatusChanged?.Invoke($"保存索引失败: {ex.Message}");
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _savePending, 0);
                                }
                            }); // intentionally no cancellation token – always persist when rebuild succeeds
                        }
                    }
                }
                catch (OperationCanceledException) { /* expected */ }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"索引更新失败: {ex.Message}");
                }
            }, token);
        }

        /// <summary>Force an immediate rebuild (called from UI "Rebuild index" button)</summary>
        public void ForceRebuild()
        {
            RebuildIndexBackground();
        }

        // -------------------------------------------------------------------
        // Search – runs on background thread, calls callback with results
        // -------------------------------------------------------------------
        public void Search(string query, bool matchCase, bool wholeWord,
            Action<List<FileRecord>> callback, CancellationToken ct)
        {
            Task.Run(() =>
            {
                var results = DoSearch(query, matchCase, wholeWord, ct);
                if (!ct.IsCancellationRequested)
                    callback(results);
            }, ct);
        }

        private List<FileRecord> DoSearch(string query, bool matchCase, bool wholeWord, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<FileRecord>();

            var index = _index; // snapshot
            var results = new List<FileRecord>(1024);

            // Support space-separated multi-term AND search (like Everything)
            string[] terms = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            StringComparison cmp = matchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            foreach (var r in index)
            {
                if (ct.IsCancellationRequested) break;
                if (Matches(r.Name, terms, cmp, wholeWord))
                    results.Add(r);
            }

            return results;
        }

        private static bool Matches(string name, string[] terms, StringComparison cmp, bool wholeWord)
        {
            foreach (var term in terms)
            {
                if (wholeWord)
                {
                    // whole word: surrounded by non-alphanumeric or at boundaries
                    int idx = 0;
                    bool found = false;
                    while ((idx = name.IndexOf(term, idx, cmp)) >= 0)
                    {
                        bool leftOk = idx == 0 || !char.IsLetterOrDigit(name[idx - 1]);
                        bool rightOk = idx + term.Length >= name.Length ||
                                       !char.IsLetterOrDigit(name[idx + term.Length]);
                        if (leftOk && rightOk) { found = true; break; }
                        idx++;
                    }
                    if (!found) return false;
                }
                else
                {
                    if (name.IndexOf(term, cmp) < 0) return false;
                }
            }
            return true;
        }
    }
}
