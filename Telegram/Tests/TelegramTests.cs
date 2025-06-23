#if(DEBUG)

using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PluginTestLibrary;

namespace FileFlows.Telegram.Tests;

[TestClass]
public class TelegramTests : TestBase
{
    private PluginSettings BuildSettings() => new()
    {
        BotToken = "bot-token",
        ChatId = "chat-id",
        TopicIdMapping = [
            new("topic-1", "topic-id-1"),
            new("topic-2", "topic-id-2"),
            new("missing-topic-id", string.Empty),
        ]
    };

    /// <summary>
    /// Tests a basic success
    /// </summary>
    [TestMethod]
    public void Success()
    {
        var args = GetNodeParameters(TempFile);
        args.GetPluginSettingsJson = _ => JsonSerializer.Serialize(BuildSettings());
        args.RenderTemplate = template => template;

        var element = new Communication.Telegram
        {
            SendMessage = (botToken, chatId, topicId, message) => (true, "sent"),
            Message = "a message",
        };
        Assert.AreEqual(1, element.Execute(args));
    }

    /// <summary>
    /// Tests a basic failure
    /// </summary>
    [TestMethod]
    public void Fail()
    {
        var args = GetNodeParameters(TempFile);
        args.GetPluginSettingsJson = _ => JsonSerializer.Serialize(BuildSettings());
        args.RenderTemplate = template => template;

        var element = new Communication.Telegram
        {
            SendMessage = (botToken, chatId, topicId, message) => (false, "failed"),
            Message = "a message",
        };
        Assert.AreEqual(2, element.Execute(args));
    }

    /// <summary>
    /// Tests a no settings fails
    /// </summary>
    [TestMethod]
    public void NoSettings()
    {
        var args = GetNodeParameters(TempFile);
        args.GetPluginSettingsJson = _ => string.Empty;
        args.RenderTemplate = template => template;

        var element = new Communication.Telegram
        {
            SendMessage = (botToken, chatId, topicId, message) => (true, "failed"),
            Message = "a message",
        };
        Assert.AreEqual(2, element.Execute(args));
    }

    /// <summary>
    /// Tests a no token fails
    /// </summary>
    [TestMethod]
    public void NoToken()
    {
        var args = GetNodeParameters(TempFile);
        var settings = BuildSettings();
        settings.BotToken = string.Empty;
        args.GetPluginSettingsJson = _ => JsonSerializer.Serialize(settings);
        args.RenderTemplate = template => template;

        var element = new Communication.Telegram
        {
            SendMessage = (botToken, chatId, topicId, message) => (true, "failed"),
            Message = "a message",
        };
        Assert.AreEqual(2, element.Execute(args));
    }

    /// <summary>
    /// Tests a no chat id fails success
    /// </summary>
    [TestMethod]
    public void NoChatId()
    {
        var args = GetNodeParameters(TempFile);
        var settings = BuildSettings();
        settings.ChatId = string.Empty;
        args.GetPluginSettingsJson = _ => JsonSerializer.Serialize(settings);
        args.RenderTemplate = template => template;

        var element = new Communication.Telegram
        {
            SendMessage = (botToken, chatId, topicId, message) => (true, "sent"),
            Message = "a message",
        };
        Assert.AreEqual(2, element.Execute(args));
    }

    /// <summary>
    /// Tests for error if ErrorOnUnmatchedTopic
    /// </summary>
    [TestMethod]
    public void ErrorOnUnMatchedTopic()
    {
        var args = GetNodeParameters(TempFile);
        var settings = BuildSettings();
        settings.TopicIdMapping.Clear();
        args.GetPluginSettingsJson = _ => JsonSerializer.Serialize(settings);
        args.RenderTemplate = template => template;

        var element = new Communication.Telegram
        {
            SendMessage = (botToken, chatId, topicId, message) => (true, "sent"),
            Message = "a message",
            TopicName = "topic-1",
        };
        Assert.AreEqual(1, element.Execute(args));

        element.ErrorOnUnmatchedTopic = true;
        Assert.AreEqual(2, element.Execute(args));
    }
}

#endif