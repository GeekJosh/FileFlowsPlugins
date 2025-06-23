namespace FileFlows.Telegram;

/// <summary>
/// The plugin settings for this plugin
/// </summary>
public class PluginSettings : IPluginSettings
{
    /// <summary>
    /// Gets or sets the bot token
    /// </summary>
    [Text(1)]
    [Required]
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the chat ID
    /// </summary>
    [Text(2)]
    [Required]
    public string ChatId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the mapping for topic names to their ID
    /// </summary>
    [KeyValue(3, null)]
    public List<KeyValuePair<string, string>> TopicIdMapping { get; set; } = [];
}