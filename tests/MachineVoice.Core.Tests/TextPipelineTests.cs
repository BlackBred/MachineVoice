using MachineVoice.Core;

namespace MachineVoice.Core.Tests;

public class TextPipelineTests
{
    [Fact]
    public void Markdown_BecomesSentences_AndCodeBlocksArePlaceholders()
    {
        const string markdown = """
            # Итог

            Исправил **ошибку** в `SpeechEngine.cs`

            ```csharp
            var x = 1;
            ```

                indented();

            - пункт [PR](https://github.com/org/repo/pull/12)
            - [x] готово
            > цитата

            ---

            | Файл | Статус |
            |---|---|
            | a.cs | ok |

            <div>html</div>

            ![картинка](https://example.com/a.png) Конец
            """;

        Assert.Equal(
            "Итог.\nИсправил ошибку в SpeechEngine.cs.\nТут пример кода.\nпункт PR.\nготово.\nцитата.\nФайл, Статус.\na.cs, ok.\nКонец.",
            MarkdownProcessor.ToSpeech(markdown));
    }

    [Theory]
    [InlineData("см. https://github.com/org/repo/pull/12.", "см. github.com.")]
    [InlineData("открой www.example.com/docs", "открой example.com")]
    [InlineData("http://localhost:5000/api?x=1, потом", "localhost, потом")]
    [InlineData("файл file:///Users/me/a/notes.md", "файл notes.md")]
    [InlineData("без ссылок", "без ссылок")]
    public void Urls_AreReplacedWithTheHost(string input, string expected) =>
        Assert.Equal(expected, UrlShortener.Shorten(input));

    [Theory]
    [InlineData("в /Users/me/Sources/App/Program.cs:12 ошибка", "в Program.cs ошибка")]
    [InlineData("см. src/MachineVoice.Core/Engine/SpeechEngine.cs.", "см. SpeechEngine.cs.")]
    [InlineData("запусти ./build.sh", "запусти build.sh")]
    [InlineData("папка ~/Library/Logs/", "папка Logs")]
    [InlineData("каталог src/a/b", "каталог b")]
    [InlineData("tests/Foo.cs:10:5", "Foo.cs")]
    [InlineData("и/или", "и/или")]
    [InlineData("TCP/IP и read/write", "TCP/IP и read/write")]
    [InlineData("1/2 и 1.5/2.0", "1/2 и 1.5/2.0")]
    public void Paths_AreReplacedWithTheLastSegment(string input, string expected) =>
        Assert.Equal(expected, PathShortener.Shorten(input));

    [Fact]
    public void Heading_HasSourceAndProject()
    {
        Assert.Equal("Cursor, проект MachineVoice.", SpeechHeading.Of("cursor", "MachineVoice"));
        Assert.Equal("Cursor.", SpeechHeading.Of("cursor", null));
        Assert.Equal("Claude code.", SpeechHeading.Of("claude-code", " "));
    }

    [Fact]
    public void Topic_IsTheShortFirstSentence()
    {
        Assert.Equal("Реализуй step 3", SpeechHeading.Topic("Реализуй step 3. Потом прогони тесты."));
        Assert.Equal("Почини Program.cs", SpeechHeading.Topic("Почини `/Users/me/App/Program.cs`?"));
        var topic = string.Join(' ', Enumerable.Range(1, 20).Select(i => "слово" + i));
        var expected = string.Join(' ', Enumerable.Range(1, SpeechHeading.MaxTopicWords).Select(i => "слово" + i)) + "…";
        Assert.Equal(expected, SpeechHeading.Topic(topic));
    }

    [Fact]
    public async Task Rules_RunInOrder()
    {
        var speech = await TextPipeline.Rules().RunAsync(
            "Готово, см. [отчёт](https://ci.example.com/run/1) и `src/App/Program.cs`.\n\n```\ncode\n```",
            new SpeechContext("cursor", "App", "почини сборку"));

        Assert.Equal("Готово, см. отчёт и Program.cs.\nТут пример кода.", speech);
    }

    [Fact]
    public async Task Rules_FinishSynchronously()
    {
        var run = TextPipeline.Rules().RunAsync("text", new SpeechContext("cursor", null, null));
        Assert.True(run.IsCompletedSuccessfully);
        Assert.Equal("text.", await run);
    }
}
