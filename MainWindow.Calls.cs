using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notify.Desktop;

public partial class MainWindow
{
    private CallRow[] callHistory = [];
    private TraceEvent[] callEvents = [];
    private CancellationTokenSource? callQuery;
    private CancellationTokenSource? callReload;
    private string? callReport;

    private NotifyHttp CallsApi()
    {
        var origin = LocalSettings.HttpsOrigin(Origin.Text.Trim());
        return new NotifyHttp(origin, RequireToken(origin));
    }

    private async void RefreshCalls(object sender, RoutedEventArgs e) => await RefreshCallsAsync();

    private async Task RefreshCallsAsync()
    {
        callReload?.Cancel();
        callQuery?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        callReload = request;
        CallsStatus.Text = "Загружаем звонки…";
        try
        {
            var rows = await CallsApi().GetAsync<CallRow[]>("/api/v1/calls", request.Token);
            if (callReload != request) return;
            callHistory = rows ?? throw new NotifyError("Сервер не предоставил историю звонков.");
            ApplyCallFilter();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is NotifyError or HttpRequestException or JsonException or OperationCanceledException)
        { if (callReload == request) CallsStatus.Text = $"История недоступна. {Friendly(error)}"; }
        finally { if (callReload == request) callReload = null; }
    }

    private void FilterCalls(object sender, RoutedEventArgs e) => ApplyCallFilter();

    private void ApplyCallFilter()
    {
        if (CallsList is null || CallDay is null || CallStatusFilter is null) return;
        var search = CallSearch.Text.Trim();
        var day = CallDay.SelectedDate;
        var status = (CallStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var rows = callHistory.Where(call => (day is null || call.LocalTime.Date == day.Value.Date) &&
            (string.IsNullOrEmpty(status) || (status == "active" ? call.Status != "ended" :
                status == "ended" ? call.Status == "ended" && call.Reason is "ended" or "playback_sent" :
                call.Status == "ended" && call.Reason is not ("ended" or "playback_sent"))) &&
            (search.Length == 0 || (call.Id + " " + call.SourceId + " " + call.Target + " " + call.Summary)
                .Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        var selected = (CallsList.SelectedItem as CallRow)?.Id;
        CallsList.ItemsSource = rows;
        CallsList.SelectedItem = rows.FirstOrDefault(row => row.Id == selected);
        CallsStatus.Text = rows.Length == 0 ? "Звонков по этим условиям нет." : $"Звонков · {rows.Length}";
    }

    private async void SelectCall(object sender, SelectionChangedEventArgs e)
    {
        if (CallDetail is null) return;
        callQuery?.Cancel();
        callReport = null;
        CopyCallReport.IsEnabled = false;
        callEvents = [];
        CallTimeline.Children.Clear();
        CallEvents.ItemsSource = null;
        CallTurns.ItemsSource = null;
        CallEventDetails.Text = "";
        if (CallsList.SelectedItem is not CallRow row)
        {
            CallDetail.Visibility = Visibility.Collapsed;
            return;
        }
        CallDetail.Visibility = Visibility.Visible;
        CallSummary.Text = row.Summary;
        CallIdentity.Text = $"ID {row.Id}\nИсточник {row.SourceId}\nСессия {row.SessionId ?? "Нет данных"}";
        CallTraceStatus.Text = "Загружаем этапы…";
        CallTranscriptStatus.Text = "Загружаем реплики…";
        using var request = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        callQuery = request;
        try
        {
            var api = CallsApi();
            var trace = await api.GetAsync<TraceData>($"/api/v1/calls/{row.Id}/trace", request.Token);
            if (callQuery != request) return;
            callEvents = trace?.Events ?? [];
            CallEvents.ItemsSource = callEvents;
            CallTraceStatus.Text = callEvents.Length == 0 ? "Нет данных об этапах." :
                trace?.Truncated == true ? "Достигнут предел записи событий." :
                row.Status == "ended" ? "Этапы звонка" : "Звонок ещё продолжается.";
            DrawCallTimeline();
            callReport = JsonSerializer.Serialize(new { call = row.Id, row.Status, row.Reason, row.Target, trace }, LocalSettings.Json);
            CopyCallReport.IsEnabled = true;
            var transcript = await api.GetAsync<TranscriptData>($"/api/v1/calls/{row.Id}/transcript", request.Token);
            if (callQuery != request) return;
            CallTurns.ItemsSource = transcript?.Turns ?? [];
            CallTranscriptStatus.Text = transcript is null || transcript.Turns.Length == 0 ? "Нет данных о репликах." :
                transcript.Partial ? "Часть реплик содержит ошибки." : "Реплики звонка";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is NotifyError or HttpRequestException or JsonException or OperationCanceledException)
        { if (callQuery == request) CallsStatus.Text = $"Данные звонка недоступны. {Friendly(error)}"; }
        finally { if (callQuery == request) callQuery = null; }
    }

    private void DrawCallTimeline(object sender, SizeChangedEventArgs e) => DrawCallTimeline();

    private void DrawCallTimeline()
    {
        if (CallTimeline is null) return;
        CallTimeline.Children.Clear();
        if (callEvents.Length == 0) return;
        var scale = Math.Max(1, CallTimeline.ActualWidth - 15) / Math.Max(1, callEvents.Max(stage => stage.OffsetMs));
        foreach (var stage in callEvents)
        {
            var bar = new Rectangle { Width = 7, Height = 10, RadiusX = 2, RadiusY = 2,
                Fill = stage.Status is "failed" or "interrupted" ? Brushes.IndianRed : (Brush)Resources["AccentFill"],
                ToolTip = stage.Label };
            Canvas.SetLeft(bar, stage.OffsetMs * scale);
            Canvas.SetTop(bar, stage.Source switch { "asterisk" => 4, "agent" => 19, _ => 34 });
            bar.MouseLeftButtonUp += (_, _) => { CallEvents.SelectedItem = stage; CallEvents.ScrollIntoView(stage); };
            CallTimeline.Children.Add(bar);
        }
    }

    private void ShowCallEvent(object sender, SelectionChangedEventArgs e) =>
        CallEventDetails.Text = CallEvents.SelectedItem is TraceEvent stage
            ? $"Точка +{stage.OffsetMs:F0} мс · {stage.Source}\nРеплика {stage.TurnId ?? "Нет данных"}\n" +
              $"Отправлено {Number(stage.Frames)} · принято {Number(stage.ReceivedFrames)} · отклонено {Number(stage.DroppedFrames)}\n" +
              $"Токены {Number(stage.Tokens)}" : "";

    private void CopyCallDiagnostics(object sender, RoutedEventArgs e)
    {
        if (callReport is null) return;
        try { System.Windows.Clipboard.SetText(callReport); CallsStatus.Text = "Диагностика скопирована."; }
        catch (System.Runtime.InteropServices.ExternalException) { CallsStatus.Text = "Буфер обмена занят."; }
    }

    private static string Number(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "Нет данных";

    private sealed record CallRow(string Id, string SourceId, string? SessionId, string Direction, string Target,
        long StartedAt, string Status, string? Reason, int DurationMs)
    {
        internal DateTimeOffset LocalTime => DateTimeOffset.FromUnixTimeMilliseconds(StartedAt).ToLocalTime();
        public string Summary => $"{LocalTime:g} · {(Direction == "incoming" ? "Входящий" : "Исходящий")} · {Target} · {SourceId[..Math.Min(8, SourceId.Length)]} · {DurationMs / 1000d:F1} с · {Result}";
        private string Result => (Reason ?? Status) switch
        {
            "active" => "Идёт разговор", "ringing" => "Вызов", "connecting" => "Соединение",
            "ended" => "Завершён", "playback_sent" => "Аудио отправлено", "missed" => "Пропущен",
            "rejected" => "Отклонён", "busy" => "Занято", "disconnected" => "Соединение потеряно",
            "timeout" => "Тайм-аут", "interrupted" => "Прерван", _ => Reason ?? Status
        };
    }

    private sealed record TraceData(string CallId, TraceEvent[] Events, bool Truncated, long UpdatedAt);
    private sealed record TraceEvent(int Sequence, string Stage, string Status, long At, double OffsetMs, string Source,
        string? TurnId, double? DurationMs, string? Code, long? Frames, long? ReceivedFrames, long? DroppedFrames, long? Tokens)
    {
        public string Label => $"{Sequence}. {Stage} · {Status}" +
            (DurationMs is { } duration ? $" · {duration:F0} мс" : "") + (Code is null ? "" : $" · {Code}");
    }

    private sealed record TranscriptData(TranscriptTurn[] Turns, bool Partial);
    private sealed record TranscriptTurn(string Speaker, string Text, string? Status, string? Error)
    {
        public string Label => $"{(Speaker == "user" ? "Вы" : "Агент")} · {Text}" +
            (Status == "error" ? $" · {Error ?? "Ошибка"}" : "");
    }
}
