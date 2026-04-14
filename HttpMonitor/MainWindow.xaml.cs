using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HttpMonitor
{
    public class RequestLogEntry
    {
        public string Timestamp  { get; set; } = "";
        public string Method     { get; set; } = "";
        public string Url        { get; set; } = "";
        public int    StatusCode { get; set; }
        public long   ElapsedMs  { get; set; }
        public string Headers    { get; set; } = "";
        public string Body       { get; set; } = "";
        public DateTime RawTime  { get; set; }
    }

    public partial class MainWindow : Window
    {
        // ─── Server state ───────────────────────────────────────
        private HttpListener?            _listener;
        private CancellationTokenSource? _serverCts;
        private bool     _serverRunning;
        private DateTime _serverStartTime;
        private int  _getCount;
        private int  _postCount;
        private long _totalProcessingMs;
        private readonly ConcurrentBag<StoredMessage> _messages = new();

        // ─── Logging ────────────────────────────────────────────
        private readonly ObservableCollection<RequestLogEntry> _allLogs      = new();
        private readonly ObservableCollection<RequestLogEntry> _filteredLogs = new();
        private readonly object _logLock = new();
        private string _logFilePath = "";

        // ─── Chart ──────────────────────────────────────────────
        private LineSeries<DateTimePoint>? _chartSeries;

        // ─── HTTP Client ────────────────────────────────────────
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // ─── Timers ─────────────────────────────────────────────
        private DispatcherTimer? _uptimeTimer;

        public MainWindow()
        {
            InitializeComponent();

            // Таблица мониторинга показывает ВСЕ логи
            RequestGrid.ItemsSource = _allLogs;

            _chartSeries = new LineSeries<DateTimePoint>
            {
                Values         = new ObservableCollection<DateTimePoint>(),
                Name           = "Запросы",
                GeometrySize   = 6,
                LineSmoothness = 0.3,
                Stroke         = new SolidColorPaint(SKColors.DodgerBlue) { StrokeThickness = 2 },
                GeometryStroke = new SolidColorPaint(SKColors.DodgerBlue) { StrokeThickness = 2 },
                Fill           = null
            };

            RequestChart.Series = new ISeries[] { _chartSeries };
            RequestChart.XAxes  = new Axis[]
            {
                new Axis
                {
                    Labeler = v => new DateTime((long)v).ToString("HH:mm"),
                    Name    = "Время"
                }
            };
            RequestChart.YAxes = new Axis[]
            {
                new Axis { Name = "Кол-во запросов", MinLimit = 0 }
            };
        }

        // ════════════════════════════════════════════════════════
        //  SERVER
        // ════════════════════════════════════════════════════════

        private async void StartStopServer_Click(object sender, RoutedEventArgs e)
        {
            if (_serverRunning)
            {
                StopServer();
                return;
            }

            if (!int.TryParse(PortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Введите корректный порт (1-65535).", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                MessageBox.Show(
                    $"Не удалось запустить сервер на порту {port}.\n{ex.Message}\n\n" +
                    "Попробуйте запустить приложение от имени администратора или выберите другой порт.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                _listener = null;
                return;
            }

            _serverRunning     = true;
            _serverStartTime   = DateTime.Now;
            _getCount          = 0;
            _postCount         = 0;
            _totalProcessingMs = 0;
            _logFilePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, $"logs_{port}.txt");

            StartStopServerBtn.Content = "Остановить сервер";
            PortBox.IsEnabled          = false;
            ServerStatusIndicator.Fill = System.Windows.Media.Brushes.LimeGreen;
            ServerStatusText.Text      = $"Работает (:{port})";

            _serverCts = new CancellationTokenSource();
            StartUptimeTimer();

            AppendServerLog($"[{DateTime.Now:HH:mm:ss}] Сервер запущен на http://localhost:{port}/");

            // FIX: запускаем в фоне через Task.Run, не await в UI-потоке
            _ = Task.Run(() => AcceptRequestsAsync(_serverCts.Token));
        }

        private void StopServer()
        {
            _serverCts?.Cancel();
            try { _listener?.Stop();  } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;

            _serverRunning = false;
            _uptimeTimer?.Stop();

            StartStopServerBtn.Content = "Запустить сервер";
            PortBox.IsEnabled          = true;
            ServerStatusIndicator.Fill = System.Windows.Media.Brushes.Gray;
            ServerStatusText.Text      = "Остановлен";

            AppendServerLog($"[{DateTime.Now:HH:mm:ss}] Сервер остановлен.");
        }

        // FIX: теперь Task (не async void), вызывается из Task.Run
        private async Task AcceptRequestsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException)     { break; }
                catch (HttpListenerException)       { break; }

                _ = Task.Run(() => HandleRequestAsync(context), CancellationToken.None);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var sw       = Stopwatch.StartNew();
            var request  = context.Request;
            var response = context.Response;

            string method     = request.HttpMethod;
            string url        = request.RawUrl ?? "/";
            string headersStr = FormatHeaders(request.Headers);
            string bodyStr    = "";

            if (request.HasEntityBody)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                bodyStr = await reader.ReadToEndAsync();
            }

            byte[] buffer;
            int statusCode = 200;

            try
            {
                if (method == "GET")
                {
                    Interlocked.Increment(ref _getCount);
                    var info = new
                    {
                        status    = "running",
                        uptime    = (DateTime.Now - _serverStartTime).ToString(@"hh\:mm\:ss"),
                        totalRequests       = _getCount + _postCount,
                        getRequests         = _getCount,
                        postRequests        = _postCount,
                        averageProcessingMs = (_getCount + _postCount) > 0
                            ? _totalProcessingMs / (_getCount + _postCount) : 0
                    };
                    buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(info, Formatting.Indented));
                    response.ContentType = "application/json; charset=utf-8";
                }
                else if (method == "POST")
                {
                    Interlocked.Increment(ref _postCount);

                    if (string.IsNullOrWhiteSpace(bodyStr))
                    {
                        statusCode = 400;
                        buffer     = Encoding.UTF8.GetBytes(
                            JsonConvert.SerializeObject(new { error = "Тело запроса пустое." }));
                        response.ContentType = "application/json; charset=utf-8";
                    }
                    else
                    {
                        try
                        {
                            var parsed   = JObject.Parse(bodyStr);
                            string msg   = parsed["message"]?.ToString() ?? "";
                            var id       = Guid.NewGuid().ToString("N")[..8];
                            _messages.Add(new StoredMessage { Id = id, Message = msg, ReceivedAt = DateTime.Now });

                            buffer = Encoding.UTF8.GetBytes(
                                JsonConvert.SerializeObject(
                                    new { id, message = msg, status = "saved" }, Formatting.Indented));
                            response.ContentType = "application/json; charset=utf-8";
                        }
                        catch (JsonReaderException)
                        {
                            statusCode = 400;
                            buffer     = Encoding.UTF8.GetBytes(
                                JsonConvert.SerializeObject(new { error = "Некорректный JSON." }));
                            response.ContentType = "application/json; charset=utf-8";
                        }
                    }
                }
                else
                {
                    statusCode = 405;
                    buffer     = Encoding.UTF8.GetBytes(
                        JsonConvert.SerializeObject(new { error = $"Метод {method} не поддерживается." }));
                    response.ContentType = "application/json; charset=utf-8";
                }
            }
            catch (Exception ex)
            {
                statusCode = 500;
                buffer     = Encoding.UTF8.GetBytes(
                    JsonConvert.SerializeObject(new { error = ex.Message }));
                response.ContentType = "application/json; charset=utf-8";
            }

            sw.Stop();
            Interlocked.Add(ref _totalProcessingMs, sw.ElapsedMilliseconds);

            response.StatusCode      = statusCode;
            response.ContentLength64 = buffer.Length;
            try
            {
                await response.OutputStream.WriteAsync(buffer);
                response.OutputStream.Close();
            }
            catch { }

            var entry = new RequestLogEntry
            {
                Timestamp  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Method     = method,
                Url        = url,
                StatusCode = statusCode,
                ElapsedMs  = sw.ElapsedMilliseconds,
                Headers    = headersStr,
                Body       = bodyStr,
                RawTime    = DateTime.Now
            };

            // FIX: сначала файл (на фоновом потоке), потом UI через Dispatcher
            WriteLogToFile(entry);

            Dispatcher.Invoke(() =>
            {
                AddLogEntry(entry);
                UpdateStats();
            });
        }

        // ════════════════════════════════════════════════════════
        //  HTTP CLIENT
        // ════════════════════════════════════════════════════════

        private async void SendRequest_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Введите URL.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string method = (MethodCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "GET";
            string body   = RequestBodyBox.Text;

            ResponseBox.Text = "Отправка запроса...";
            var sendBtn = (Button)sender;
            sendBtn.IsEnabled = false;

            try
            {
                HttpResponseMessage httpResponse;
                var sw = Stopwatch.StartNew();

                if (method == "POST")
                {
                    var content = new StringContent(body, Encoding.UTF8, "application/json");
                    httpResponse = await _httpClient.PostAsync(url, content);
                }
                else
                {
                    httpResponse = await _httpClient.GetAsync(url);
                }

                sw.Stop();

                string responseBody = await httpResponse.Content.ReadAsStringAsync();
                try
                {
                    var obj = JToken.Parse(responseBody);
                    responseBody = obj.ToString(Formatting.Indented);
                }
                catch { }

                var sb = new StringBuilder();
                sb.AppendLine($"Статус: {(int)httpResponse.StatusCode} {httpResponse.StatusCode}");
                sb.AppendLine($"Время: {sw.ElapsedMilliseconds} мс");
                sb.AppendLine("─── Заголовки ответа ───");
                foreach (var h in httpResponse.Headers)
                    sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                foreach (var h in httpResponse.Content.Headers)
                    sb.AppendLine($"  {h.Key}: {string.Join(", ", h.Value)}");
                sb.AppendLine("─── Тело ответа ───");
                sb.AppendLine(responseBody);

                ResponseBox.Text = sb.ToString();
            }
            catch (HttpRequestException ex)    { ResponseBox.Text = $"Ошибка HTTP: {ex.Message}"; }
            catch (TaskCanceledException)       { ResponseBox.Text = "Ошибка: таймаут запроса (30 сек)."; }
            catch (Exception ex)               { ResponseBox.Text = $"Ошибка: {ex.Message}"; }
            finally                            { sendBtn.IsEnabled = true; }
        }

        // ════════════════════════════════════════════════════════
        //  LOGGING & FILTERING
        // ════════════════════════════════════════════════════════

        // FIX: вызывается ТОЛЬКО из UI-потока (через Dispatcher.Invoke)
        private void AddLogEntry(RequestLogEntry entry)
        {
            _allLogs.Add(entry);

            if (MatchesFilter(entry))
                _filteredLogs.Add(entry);

            var sb = new StringBuilder();
            sb.AppendLine($"[{entry.Timestamp}] {entry.Method} {entry.Url} → {entry.StatusCode} ({entry.ElapsedMs} мс)");
            if (!string.IsNullOrEmpty(entry.Headers))
                sb.AppendLine($"  Заголовки: {entry.Headers}");
            if (!string.IsNullOrEmpty(entry.Body))
                sb.AppendLine($"  Тело: {entry.Body}");
            ServerLogBox.AppendText(sb.ToString());
            ServerLogBox.ScrollToEnd();

            // FIX: обновляем график сразу после добавления записи
            UpdateChartData();
        }

        private bool MatchesFilter(RequestLogEntry entry)
        {
            if (LogFilterCombo.SelectedItem is not ComboBoxItem item)
                return true;

            string filter = item.Content?.ToString() ?? "Все";
            if (filter == "Все") return true;
            if (filter == "GET" || filter == "POST") return entry.Method == filter;
            if (int.TryParse(filter, out int code)) return entry.StatusCode == code;
            return true;
        }

        private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_allLogs == null) return;

            _filteredLogs.Clear();
            ServerLogBox?.Clear();

            foreach (var entry in _allLogs)
            {
                if (MatchesFilter(entry))
                {
                    _filteredLogs.Add(entry);
                    var sb = new StringBuilder();
                    sb.AppendLine($"[{entry.Timestamp}] {entry.Method} {entry.Url} → {entry.StatusCode} ({entry.ElapsedMs} мс)");
                    if (!string.IsNullOrEmpty(entry.Headers))
                        sb.AppendLine($"  Заголовки: {entry.Headers}");
                    if (!string.IsNullOrEmpty(entry.Body))
                        sb.AppendLine($"  Тело: {entry.Body}");
                    ServerLogBox?.AppendText(sb.ToString());
                }
            }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _allLogs.Clear();
            _filteredLogs.Clear();
            ServerLogBox.Clear();
            // FIX: сбрасываем серию
            if (_chartSeries != null)
                _chartSeries.Values = new ObservableCollection<DateTimePoint>();
        }

        private void SaveLogs_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = "logs",
                DefaultExt = ".txt",
                Filter     = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    foreach (var entry in _allLogs)
                    {
                        sb.AppendLine($"[{entry.Timestamp}] {entry.Method} {entry.Url} → {entry.StatusCode} ({entry.ElapsedMs} мс)");
                        if (!string.IsNullOrEmpty(entry.Headers))
                            sb.AppendLine($"  Заголовки: {entry.Headers}");
                        if (!string.IsNullOrEmpty(entry.Body))
                            sb.AppendLine($"  Тело: {entry.Body}");
                        sb.AppendLine();
                    }
                    File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show($"Логи сохранены в {dialog.FileName}", "Готово",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка сохранения: {ex.Message}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void WriteLogToFile(RequestLogEntry entry)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;
            try
            {
                var line = $"[{entry.Timestamp}] {entry.Method} {entry.Url} → {entry.StatusCode} ({entry.ElapsedMs} мс) | Headers: {entry.Headers} | Body: {entry.Body}\n";
                lock (_logLock)
                {
                    File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                }
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════
        //  CHART & STATS
        // ════════════════════════════════════════════════════════

        private void UpdateStats()
        {
            GetCountText.Text  = _getCount.ToString();
            PostCountText.Text = _postCount.ToString();
            int total = _getCount + _postCount;
            TotalCountText.Text = total.ToString();
            AvgTimeText.Text    = total > 0 ? $"{_totalProcessingMs / total} мс" : "0 мс";
        }

        // FIX: заменяем Values целиком — это заставляет LiveCharts перерисовать график
        private void UpdateChartData()
        {
            bool byMinute = true;
            if (ChartIntervalCombo.SelectedItem is ComboBoxItem ci)
                byMinute = ci.Content?.ToString() == "По минутам";

            var grouped = _allLogs
                .GroupBy(e => byMinute
                    ? new DateTime(e.RawTime.Year, e.RawTime.Month, e.RawTime.Day,
                                   e.RawTime.Hour, e.RawTime.Minute, 0)
                    : new DateTime(e.RawTime.Year, e.RawTime.Month, e.RawTime.Day,
                                   e.RawTime.Hour, 0, 0))
                .OrderBy(g => g.Key)
                .Select(g => new DateTimePoint(g.Key, g.Count()))
                .ToList();

            if (_chartSeries != null)
                _chartSeries.Values = new ObservableCollection<DateTimePoint>(grouped);
        }

        private void ChartInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_allLogs == null || _allLogs.Count == 0) return;
            UpdateChartData();
        }

        // ════════════════════════════════════════════════════════
        //  UPTIME TIMER
        // ════════════════════════════════════════════════════════

        private void StartUptimeTimer()
        {
            _uptimeTimer?.Stop();
            _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uptimeTimer.Tick += (_, _) =>
            {
                if (_serverRunning)
                    UptimeText.Text = (DateTime.Now - _serverStartTime).ToString(@"hh\:mm\:ss");
            };
            _uptimeTimer.Start();
        }

        // ════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════

        private static string FormatHeaders(System.Collections.Specialized.NameValueCollection headers)
        {
            var parts = new List<string>();
            foreach (string? key in headers.AllKeys)
                if (key != null)
                    parts.Add($"{key}: {headers[key]}");
            return string.Join("; ", parts);
        }

        private void AppendServerLog(string text)
        {
            ServerLogBox.AppendText(text + "\n");
            ServerLogBox.ScrollToEnd();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_serverRunning)
                StopServer();
        }

        private class StoredMessage
        {
            public string   Id         { get; set; } = "";
            public string   Message    { get; set; } = "";
            public DateTime ReceivedAt { get; set; }
        }
    }
}
