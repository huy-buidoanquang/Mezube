namespace Mezube.Ui;

/// <summary>
/// Unique Mezon button ids, same pattern as pm-assistant-bot:
/// <c>{function}{messageId19}{userId19}:{action}</c> or with payload
/// <c>{function}{messageId19}{userId19}:{action}:{extra}</c>.
/// </summary>
public static class MezubeButtonId
{
    public const int PlayerControls = 11;
    public const int SearchPick = 12;
    public const int PlaylistImport = 13;

    public const string ActionSkip = "skip";
    public const string ActionStop = "stop";
    public const string ActionSubmit = "submit";
    public const string ActionPrev = "prev";
    public const string ActionNext = "next";
    public const string ActionConfirm = "confirm";
    public const string ActionCancel = "cancel";

    public const string RadioSearch = "mezube_radio_search";
    public const string RadioPlaylist = "mezube_radio_pl";

    private const int FunctionPrefixLength = 2;
    private const int IdLength = 19;

    /// <summary>Route prefix for <see cref="InteractionRouter.OnButton"/> (<c>"11*"</c>).</summary>
    public static string PlayerControlsPrefix => $"{PlayerControls.ToString().PadLeft(FunctionPrefixLength, '0')}*";
    public static string SearchPickPrefix => $"{SearchPick.ToString().PadLeft(FunctionPrefixLength, '0')}*";
    public static string PlaylistImportPrefix => $"{PlaylistImport.ToString().PadLeft(FunctionPrefixLength, '0')}*";

    public static string Create(int interactionFunction, long messageId, long userId, string action, string? extra = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var prefix = interactionFunction.ToString().PadLeft(FunctionPrefixLength, '0');
        var id = $"{prefix}{FormatId(messageId)}{FormatId(userId)}:{action.Trim()}";
        return string.IsNullOrWhiteSpace(extra) ? id : $"{id}:{extra.Trim()}";
    }

    public static string CreatePlayerControl(long messageId, long userId, string action, long? clanId = null)
        => Create(PlayerControls, messageId, userId, action, clanId?.ToString());

    public static string CreateSearchPick(long messageId, long userId, string action)
        => Create(SearchPick, messageId, userId, action);

    public static string CreatePlaylistImport(long messageId, long userId, string action)
        => Create(PlaylistImport, messageId, userId, action);

    public static bool TryParse(string? buttonId, out Parsed parts)
    {
        parts = default;
        if (string.IsNullOrWhiteSpace(buttonId))
        {
            return false;
        }

        var prefixLen = GetFunctionPrefixLength(buttonId);
        var minLength = prefixLen + IdLength + IdLength + 2; // + ':' + at least 1 action char
        if (buttonId.Length < minLength)
        {
            return false;
        }

        if (!int.TryParse(buttonId.AsSpan(0, prefixLen), out var function))
        {
            return false;
        }

        var messageSpan = buttonId.AsSpan(prefixLen, IdLength);
        var userSpan = buttonId.AsSpan(prefixLen + IdLength, IdLength);
        if (!long.TryParse(messageSpan, out var messageId) || !long.TryParse(userSpan, out var userId))
        {
            return false;
        }

        var delimiterIndex = prefixLen + IdLength + IdLength;
        if (buttonId[delimiterIndex] != ':')
        {
            return false;
        }

        var actionStart = delimiterIndex + 1;
        var extraIdx = buttonId.IndexOf(':', actionStart);
        string action;
        string? extra;
        if (extraIdx < 0)
        {
            action = buttonId[actionStart..];
            extra = null;
        }
        else
        {
            action = buttonId[actionStart..extraIdx];
            extra = buttonId[(extraIdx + 1)..];
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        parts = new Parsed(function, messageId, userId, action, extra);
        return true;
    }

    public readonly record struct Parsed(
        int InteractionFunction,
        long MessageId,
        long UserId,
        string Action,
        string? Extra)
    {
        public long? ClanId => long.TryParse(Extra, out var clanId) ? clanId : null;
    }

    private static string FormatId(long id)
        => id.ToString().PadLeft(IdLength, '0');

    private static int GetFunctionPrefixLength(string buttonId)
    {
        // Prefer 2-digit codes 05–99 (pm-assistant / Mezube); fall back to 1 digit for legacy.
        if (buttonId.Length >= FunctionPrefixLength + IdLength + IdLength + 2
            && char.IsDigit(buttonId[0])
            && char.IsDigit(buttonId[1]))
        {
            return FunctionPrefixLength;
        }

        return 1;
    }
}
