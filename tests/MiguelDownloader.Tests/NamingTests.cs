using MiguelDownloader.Core.Naming;
using Xunit;

namespace MiguelDownloader.Tests;

public class FileNameSanitizerTests
{
    [Theory]
    [InlineData("normal title", "normal title")]
    [InlineData("with/slash", "with-slash")]
    [InlineData("with\\backslash", "with-backslash")]
    [InlineData("with:colon", "with -colon")]
    [InlineData("with|pipe", "with-pipe")]
    [InlineData("with?question", "withquestion")]
    [InlineData("with*star", "withstar")]
    [InlineData("with<angle>brackets", "with(angle)brackets")]
    public void ReplacesInvalidCharacters(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.SanitizeComponent(input));
    }

    [Fact]
    public void NeutralisesEveryCharacterWindowsRejects()
    {
        // Asserting against the runtime's own list rather than a copy of it, so the sanitiser
        // cannot drift away from what the operating system actually refuses.
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            var result = FileNameSanitizer.SanitizeComponent($"before{invalid}after");

            Assert.DoesNotContain(invalid, result);
            Assert.False(string.IsNullOrWhiteSpace(result));
        }
    }

    [Fact]
    public void SanitisedNamesAreAcceptedByThePathApis()
    {
        // A name that still throws when combined into a path would defeat the whole exercise.
        string[] hostile =
        [
            "AC/DC: Live \"Best\" <2024> | 100%?*",
            "CON", "NUL.mp4", "trailing dot.", "trailing space ",
            "emoji 😀 title", "日本語のタイトル",
        ];

        foreach (var title in hostile)
        {
            var name = FileNameSanitizer.SanitizeComponent(title);
            var combined = Path.Combine(@"C:\out", name + ".mp4");

            Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
            Assert.False(string.IsNullOrWhiteSpace(Path.GetFileName(combined)));
        }
    }

    [Fact]
    public void RemovesControlCharacters()
    {
        var result = FileNameSanitizer.SanitizeComponent("line\u0001break\u001Fhere");
        Assert.DoesNotContain('\u0001', result);
        Assert.DoesNotContain('\u001F', result);
        Assert.Equal("line break here", result);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("CON.mp4")]
    public void EscapesReservedDeviceNames(string input)
    {
        var result = FileNameSanitizer.SanitizeComponent(input);
        Assert.False(FileNameSanitizer.IsReserved(result), $"{result} is still reserved");
        Assert.StartsWith("_", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trailing dot.")]
    [InlineData("trailing space ")]
    [InlineData("both. ")]
    public void StripsTrailingDotsAndSpaces(string input)
    {
        var result = FileNameSanitizer.SanitizeComponent(input);
        Assert.False(result.EndsWith('.'));
        Assert.False(result.EndsWith(' '));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("***")]
    public void FallsBackWhenNothingUsableRemains(string? input)
    {
        Assert.Equal("fallback", FileNameSanitizer.SanitizeComponent(input, "fallback"));
    }

    [Fact]
    public void PreservesNonAsciiCharacters()
    {
        // Accented and non-Latin titles are perfectly valid on Windows and must survive intact.
        const string title = "Coração — 日本語 — Ñandú";
        var result = FileNameSanitizer.SanitizeComponent(title);
        Assert.Contains("Coração", result, StringComparison.Ordinal);
        Assert.Contains("日本語", result, StringComparison.Ordinal);
        Assert.Contains("Ñandú", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatesLongNamesWithoutBreakingSurrogatePairs()
    {
        // Emoji are surrogate pairs; cutting one in half produces an invalid file name.
        var input = string.Concat(Enumerable.Repeat("😀", 200));
        var result = FileNameSanitizer.Truncate(input, 51);

        Assert.True(result.Length <= 51);
        Assert.False(char.IsHighSurrogate(result[^1]), "truncation split a surrogate pair");
    }

    [Fact]
    public void KeepsComponentWithinLengthLimit()
    {
        var input = new string('a', 500);
        var result = FileNameSanitizer.SanitizeComponent(input);
        Assert.True(result.Length <= FileNameSanitizer.MaxComponentLength);
    }

    [Fact]
    public void ShortensFileNameToFitThePathLimit()
    {
        var directory = @"C:\Users\someone\Downloads\" + new string('d', 150);
        var fileName = new string('f', 120) + ".mp4";

        var result = FileNameSanitizer.EnsurePathFits(directory, fileName, maxPathLength: 240);

        Assert.True(Path.Combine(directory, result).Length <= 240);
        Assert.EndsWith(".mp4", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsAUniqueNameWhenTheFileExists()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\out\video.mp4",
            @"C:\out\video (2).mp4",
        };

        var result = FileNameSanitizer.MakeUnique(@"C:\out", "video.mp4", existing.Contains);

        Assert.Equal("video (3).mp4", result);
    }

    [Fact]
    public void KeepsTheNameWhenNothingCollides()
    {
        var result = FileNameSanitizer.MakeUnique(@"C:\out", "video.mp4", _ => false);
        Assert.Equal("video.mp4", result);
    }
}

public class NameTemplateTests
{
    [Fact]
    public void ExpandsSimpleFields()
    {
        var values = NameTemplate.BuildValues(("title", "My Video"), ("id", "abc123"));
        var result = NameTemplate.Expand("%(title)s [%(id)s]", values);

        Assert.Equal("My Video [abc123]", result);
    }

    [Fact]
    public void PadsNumbers()
    {
        var values = NameTemplate.BuildValues(("track_number", 7), ("title", "Song"));
        var result = NameTemplate.Expand("%(track_number)02d - %(title)s", values);

        Assert.Equal("07 - Song", result);
    }

    [Fact]
    public void DropsSeparatorsLeftByMissingFields()
    {
        // No artist: the result must not begin with a stranded " - ".
        var values = NameTemplate.BuildValues(("title", "Song"));
        var result = NameTemplate.Expand("%(artist)s - %(title)s", values);

        Assert.Equal("Song", result);
    }

    [Fact]
    public void DropsEmptyBracketsFromMissingId()
    {
        var values = NameTemplate.BuildValues(("title", "My Video"));
        var result = NameTemplate.Expand("%(title)s [%(id)s]", values);

        Assert.Equal("My Video", result);
    }

    [Fact]
    public void FieldValuesCannotCreateDirectories()
    {
        // A title containing a slash must not escape its path component.
        var values = NameTemplate.BuildValues(("title", "AC/DC Live"));
        var result = NameTemplate.Expand("%(title)s", values, allowSubdirectories: true);

        Assert.DoesNotContain('/', result);
        Assert.Contains("AC-DC", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateCanCreateDirectoriesWhenAllowed()
    {
        var values = NameTemplate.BuildValues(
            ("album_artist", "Artist"), ("album", "Album"), ("year", 2020));

        var result = NameTemplate.Expand(
            "%(album_artist)s/%(album)s (%(year)d)", values, allowSubdirectories: true);

        Assert.Equal("Artist/Album (2020)", result);
    }

    [Fact]
    public void SkipsEmptyDirectorySegments()
    {
        var values = NameTemplate.BuildValues(("album", "Album"));
        var result = NameTemplate.Expand("%(album_artist)s/%(album)s", values, allowSubdirectories: true);

        Assert.Equal("Album", result);
    }

    [Fact]
    public void UnknownFieldsExpandToNothing()
    {
        var values = NameTemplate.BuildValues(("title", "Song"));
        var result = NameTemplate.Expand("%(title)s %(nonexistent)s", values);

        Assert.Equal("Song", result);
    }

    [Fact]
    public void ReportsUnknownTokensSoSettingsCanRejectATypo()
    {
        var unknown = NameTemplate.FindUnknownFields(
            "%(title)s - %(atrist)s", NameTemplate.KnownFields);

        Assert.Contains("atrist", unknown);
        Assert.DoesNotContain("title", unknown);
    }

    [Fact]
    public void SanitisesInvalidCharactersFromFieldValues()
    {
        var values = NameTemplate.BuildValues(("title", "What? Really: yes*"));
        var result = NameTemplate.Expand("%(title)s", values);

        Assert.DoesNotContain('?', result);
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
    }

    [Fact]
    public void DefaultTemplatesAreAllValid()
    {
        foreach (var template in new[]
        {
            NameTemplate.Defaults.Video,
            NameTemplate.Defaults.VideoSimple,
            NameTemplate.Defaults.PlaylistItem,
            NameTemplate.Defaults.MusicTrack,
            NameTemplate.Defaults.MusicSingle,
            NameTemplate.Defaults.AlbumDirectory,
            NameTemplate.Defaults.AlbumDirectoryWithYear,
            NameTemplate.Defaults.PlaylistDirectory,
        })
        {
            var unknown = NameTemplate.FindUnknownFields(template, NameTemplate.KnownFields);
            Assert.True(unknown.Count == 0, $"{template} uses unknown fields: {string.Join(", ", unknown)}");
        }
    }
}
