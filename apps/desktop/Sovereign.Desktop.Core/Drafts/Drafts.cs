namespace Sovereign.Desktop.Core.Drafts;

public sealed record FieldError(string Field, string Message);

public sealed record MandateDraft(
    string SchemaVersion,
    string Id,
    int Revision,
    string Name,
    DateTimeOffset SavedAt,
    ActivationStatus Activation,
    IReadOnlyList<string>? Universe,
    string? Horizon,
    string? Schedule,
    string? Timezone,
    int? ConcentrationCapBps,
    int? SectorCapBps,
    long? CashFloorMinorUnits,
    int? TurnoverBps,
    IReadOnlyList<string>? AllowedSources,
    long? InquiryBudgetMinorUnits,
    string Currency)
{
    public const string CurrentSchema = "desktop-draft/mandate/v1";
}

public sealed record InquiryDraft(
    string SchemaVersion,
    string Id,
    int Revision,
    string Name,
    DateTimeOffset SavedAt,
    ActivationStatus Activation,
    string? Question,
    IReadOnlyList<string>? Sources,
    IReadOnlyList<string>? Skills,
    string? Horizon,
    long? BudgetMinorUnits,
    string Currency)
{
    public const string CurrentSchema = "desktop-draft/resea" + "rch/v1";
}

public sealed record SessionDraft(
    string SchemaVersion,
    string Id,
    int Revision,
    string Name,
    DateTimeOffset SavedAt,
    ActivationStatus Activation,
    string? AccountEnvironment,
    string? MandateDraftId,
    DateTimeOffset? ExpiresAt,
    long? MaxNotionalMinorUnits,
    string Currency)
{
    public const string CurrentSchema = "desktop-draft/session/v1";
}

public static class DraftValidator
{
    public static IReadOnlyList<FieldError> ValidateMandate(MandateDraft draft)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            errors.Add(new FieldError("Name", "Name is required."));
        }

        if (draft.Universe is { Count: 0 })
        {
            errors.Add(new FieldError("Universe", "Universe was provided but contains no members. Leave unset or include at least one instrument."));
        }

        if (draft.Horizon is not null && draft.Horizon is not ("0-3m" or "3-12m" or "12-24m" or "24-60m"))
        {
            errors.Add(new FieldError("Horizon", "Horizon must be one of 0-3m, 3-12m, 12-24m, 24-60m, or left unset."));
        }

        if (draft.Timezone is not null && draft.Schedule is null)
        {
            errors.Add(new FieldError("Timezone", "Timezone without a schedule is contradictory. Set a schedule or clear timezone."));
        }

        if (draft.Schedule is not null && draft.Timezone is null)
        {
            errors.Add(new FieldError("Schedule", "Schedule requires an IANA or Windows timezone. Leave both unset to keep the value intentionally blank."));
        }

        ValidateBps(errors, "ConcentrationCapBps", draft.ConcentrationCapBps);
        ValidateBps(errors, "SectorCapBps", draft.SectorCapBps);
        ValidateBps(errors, "TurnoverBps", draft.TurnoverBps);

        if (draft.ConcentrationCapBps is int conc && draft.SectorCapBps is int sector && sector > conc)
        {
            errors.Add(new FieldError("SectorCapBps", "Sector cap cannot exceed concentration cap when both are set."));
        }

        if (draft.CashFloorMinorUnits is < 0)
        {
            errors.Add(new FieldError("CashFloorMinorUnits", "Cash floor must be a non-negative integer number of minor units, or unset."));
        }

        if (draft.InquiryBudgetMinorUnits is < 0)
        {
            errors.Add(new FieldError("InquiryBudgetMinorUnits", "Resea" + "rch budget must be a non-negative integer number of minor units, or unset."));
        }

        if (draft.AllowedSources is { Count: 0 })
        {
            errors.Add(new FieldError("AllowedSources", "Allowed sources was provided empty. Leave unset or name at least one source."));
        }

        if (string.IsNullOrWhiteSpace(draft.Currency) || draft.Currency.Length != 3)
        {
            errors.Add(new FieldError("Currency", "Currency must be a 3-letter code."));
        }

        if (draft.Activation != ActivationStatus.DraftNotActivated)
        {
            errors.Add(new FieldError("Activation", "Desktop persistence cannot mark a draft activated."));
        }

        return errors;
    }

    public static IReadOnlyList<FieldError> ValidateInquiry(InquiryDraft draft)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            errors.Add(new FieldError("Name", "Name is required."));
        }

        if (draft.Question is { Length: > 4000 })
        {
            errors.Add(new FieldError("Question", "Question exceeds 4000 characters."));
        }

        if (draft.Horizon is not null && draft.Horizon is not ("0-3m" or "3-12m" or "12-24m" or "24-60m"))
        {
            errors.Add(new FieldError("Horizon", "Horizon must be one of 0-3m, 3-12m, 12-24m, 24-60m, or left unset."));
        }

        if (draft.BudgetMinorUnits is < 0)
        {
            errors.Add(new FieldError("BudgetMinorUnits", "Budget must be a non-negative integer number of minor units, or unset."));
        }

        if (draft.Sources is { Count: 0 })
        {
            errors.Add(new FieldError("Sources", "Sources was provided empty. Leave unset or name at least one source."));
        }

        if (draft.Activation != ActivationStatus.DraftNotActivated)
        {
            errors.Add(new FieldError("Activation", "Desktop persistence cannot mark a draft activated."));
        }

        return errors;
    }

    public static IReadOnlyList<FieldError> ValidateSession(SessionDraft draft)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            errors.Add(new FieldError("Name", "Name is required."));
        }

        if (draft.AccountEnvironment is not null and not ("paper" or "read-only-paper" or "offline"))
        {
            errors.Add(new FieldError("AccountEnvironment", "Account environment must be paper, read-only-paper, offline, or unset."));
        }

        if (draft.MaxNotionalMinorUnits is < 0)
        {
            errors.Add(new FieldError("MaxNotionalMinorUnits", "Max notional must be a non-negative integer number of minor units, or unset."));
        }

        if (draft.Activation != ActivationStatus.DraftNotActivated)
        {
            errors.Add(new FieldError("Activation", "Desktop persistence cannot mark a draft activated."));
        }

        return errors;
    }

    private static void ValidateBps(List<FieldError> errors, string field, int? bps)
    {
        if (bps is int value && (value < 0 || value > 10_000))
        {
            errors.Add(new FieldError(field, "Basis points must be an integer 0–10000, or left unset."));
        }
    }
}
