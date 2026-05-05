using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ChatClient.Web.Models;

public class ChatExportDocument : IDocument
{
    private readonly string _roomName;
    private readonly string _exportedBy;
    private readonly IReadOnlyList<ChatHistoryItem> _items;

    public ChatExportDocument(string roomName, string exportedBy, IReadOnlyList<ChatHistoryItem> items)
    {
        _roomName = roomName;
        _exportedBy = exportedBy;
        _items = items;
    }

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Margin(24);
            page.PageColor("#1e1f22");

            page.Header().Column(column =>
            {
                column.Item().Text($"Chat Client - #{_roomName}")
                    .FontSize(20)
                    .Bold()
                    .FontColor(Colors.White);

                column.Item().Text($"Exported by: {_exportedBy}")
                    .FontSize(10)
                    .FontColor("#b5bac1");

                column.Item().Text($"Generated: {DateTime.Now:dd/MM/yyyy HH:mm:ss}")
                    .FontSize(10)
                    .FontColor("#b5bac1");

                column.Item().PaddingTop(8).LineHorizontal(1).LineColor("#3a3d42");
            });

            page.Content().PaddingTop(12).Column(column =>
            {
                if (_items.Count == 0)
                {
                    column.Item().Text("No visible messages in this room.")
                        .FontSize(11)
                        .FontColor("#b5bac1");
                    return;
                }

                foreach (var item in _items)
                {
                    if (item.ItemType == "status")
                    {
                        column.Item()
                            .PaddingVertical(4)
                            .AlignCenter()
                            .Text($"{item.StatusText} · {item.TimestampUtc.ToLocalTime():HH:mm:ss}")
                            .Italic()
                            .FontSize(10)
                            .FontColor("#9aa0a6");
                    }
                    else
                    {
                        var userColor = GetUserColor(item.Username);
                        var initial = string.IsNullOrWhiteSpace(item.Username)
                            ? "?"
                            : item.Username.Substring(0, 1).ToUpper();

                        column.Item().PaddingBottom(10).Row(row =>
                        {
                            row.ConstantItem(30).Height(30).AlignMiddle().AlignCenter().Background(userColor).Text(initial)
                                .FontColor(Colors.White)
                                .Bold();

                            row.RelativeItem().PaddingLeft(10).Column(msg =>
                            {
                                msg.Item().Row(meta =>
                                {
                                    meta.RelativeItem().Text(item.Username)
                                        .Bold()
                                        .FontSize(11)
                                        .FontColor(Colors.White);

                                    meta.ConstantItem(60).AlignRight().Text(item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"))
                                        .FontSize(9)
                                        .FontColor("#b5bac1");
                                });

                                msg.Item().PaddingTop(3).Background("#2a2c31").Border(1).BorderColor("#3e4148").Padding(10).Text(item.Message)
                                    .FontSize(10)
                                    .FontColor("#e6e8eb");
                            });
                        });
                    }
                }
            });

            page.Footer().AlignCenter().Text(x =>
            {
                x.Span("Page ").FontColor("#b5bac1");
                x.CurrentPageNumber().FontColor(Colors.White);
                x.Span(" / ").FontColor("#b5bac1");
                x.TotalPages().FontColor(Colors.White);
            });
        });
    }

    private string GetUserColor(string username)
    {
        string[] colors =
        [
            "#5865f2", "#57f287", "#eb459e", "#faa61a",
            "#3ba55d", "#ed4245", "#00a8fc", "#9b59b6"
        ];

        var hash = 0;
        foreach (var c in username)
            hash = c + ((hash << 5) - hash);

        return colors[Math.Abs(hash) % colors.Length];
    }
}
