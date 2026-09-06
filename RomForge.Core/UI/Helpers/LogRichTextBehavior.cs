using Common;
using RomForge.Core.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace RomForge.Core.UI.Helpers;

public static class LogRichTextBehavior
{
    public static readonly DependencyProperty EntriesProperty = DependencyProperty.RegisterAttached(
        "Entries",
        typeof(ObservableCollection<LogEntry>),
        typeof(LogRichTextBehavior),
        new PropertyMetadata(null, OnEntriesChanged));

    public static void SetEntries(DependencyObject obj, ObservableCollection<LogEntry>? value) => obj.SetValue(EntriesProperty, value);

    public static ObservableCollection<LogEntry>? GetEntries(DependencyObject obj) => (ObservableCollection<LogEntry>?)obj.GetValue(EntriesProperty);

    private static void OnEntriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox logBox)
            return;

        if (Subscriptions.Get(logBox) is { } previous)
        {
            previous.Entries.CollectionChanged -= previous.Handler;
            Subscriptions.Remove(logBox);
        }

        if (e.NewValue is not ObservableCollection<LogEntry> entries)
        {
            logBox.Document.Blocks.Clear();
            return;
        }

        NotifyCollectionChangedEventHandler handler = (_, args) =>
            logBox.Dispatcher.InvokeAsync(
                () => UpdateDocument(logBox, entries, args),
                DispatcherPriority.Background);

        entries.CollectionChanged += handler;
        Subscriptions.Set(logBox, new Subscription(entries, handler));
        RebuildDocument(logBox, entries);
    }

    private static void UpdateDocument(RichTextBox logBox, ObservableCollection<LogEntry> entries, NotifyCollectionChangedEventArgs args)
    {
        if (!ReferenceEquals(GetEntries(logBox), entries))
            return;

        if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems is not null)
        {
            foreach (LogEntry entry in args.NewItems)
                AppendEntry(logBox.Document, entry);

            ScrollToEnd(logBox);
            return;
        }

        RebuildDocument(logBox, entries);
    }

    private static void RebuildDocument(RichTextBox logBox, IEnumerable<LogEntry> entries)
    {
        var document = logBox.Document;
        document.Blocks.Clear();
        document.PagePadding = new Thickness(0);

        foreach (var entry in entries)
            AppendEntry(document, entry);

        ScrollToEnd(logBox);
    }

    private static void AppendEntry(FlowDocument document, LogEntry entry)
    {
        var paragraph = new Paragraph(new Run(entry.Message))
        {
            Foreground = GetForeground(entry.Level),
            Margin = new Thickness(0),
            Padding = new Thickness(0)
        };

        document.Blocks.Add(paragraph);
    }

    private static Brush GetForeground(LogLevel level) => level switch
    {
        LogLevel.Ok => Brushes.LimeGreen,
        LogLevel.Highlight => Brushes.Orange,
        LogLevel.Error => Brushes.Tomato,
        _ => Brushes.Gray
    };

    private static void ScrollToEnd(RichTextBox logBox)
    {
        logBox.CaretPosition = logBox.Document.ContentEnd;
        logBox.ScrollToEnd();
    }

    private static class Subscriptions
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RichTextBox, Subscription> Table = new();

        public static Subscription? Get(RichTextBox logBox) => Table.TryGetValue(logBox, out var subscription) ? subscription : null;

        public static void Set(RichTextBox logBox, Subscription subscription)
        {
            Table.Remove(logBox);
            Table.Add(logBox, subscription);
        }

        public static void Remove(RichTextBox logBox) => Table.Remove(logBox);
    }

    private sealed class Subscription(ObservableCollection<LogEntry> entries, NotifyCollectionChangedEventHandler handler)
    {
        public ObservableCollection<LogEntry> Entries { get; } = entries;
        public NotifyCollectionChangedEventHandler Handler { get; } = handler;
    }
}
