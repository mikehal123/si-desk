using System.Globalization;

namespace Sovereign.Desktop.Core.Desk;

public sealed record CapabilityDescriptor(string Id, string Version, string Effect, bool External,
    bool RequiresApproval, string InputSchemaId, string OutputSchemaId);

public static class DeskAuthority
{
    public static readonly IReadOnlyDictionary<string, CapabilityDescriptor> Capabilities = new Dictionary<string, CapabilityDescriptor>(StringComparer.Ordinal)
    {
        ["broker.model_generate"] = new("broker.model_generate", "1.0.0", "model_call", true, true, "model-request-v1", "model-result-v1"),
        ["broker.evidence_search"] = new("broker.evidence_search", "1.0.0", "evidence_search", true, true, "evidence-search-v1", "evidence-results-v1"),
        ["broker.source_fetch"] = new("broker.source_fetch", "1.0.0", "source_fetch", true, true, "source-fetch-v1", "source-result-v1"),
        ["broker.market_read"] = new("broker.market_read", "1.0.0", "market_read", true, true, "market-read-v1", "market-result-v1"),
        ["valuation_sensitivity_v1"] = new("valuation_sensitivity_v1", "1.0.0", "calculation", false, true, "valuation-input", "valuation-output"),
        ["approval.request"] = new("approval.request", "1.0.0", "request_human_approval", false, false, "approval-request-v1", "approval-result-v1")
    };

    public static void ValidateApproval(ApprovalReceipt approval, string scopeSha256, DateTimeOffset now)
    {
        if (approval.SchemaVersion != 1 || approval.ApprovalId == Guid.Empty || approval.ActorKind != "human" ||
            string.IsNullOrWhiteSpace(approval.ActorId) || approval.ScopeSha256 != scopeSha256 ||
            scopeSha256.Length != 64 || scopeSha256.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            approval.ApprovedAt.Offset != TimeSpan.Zero || now.Offset != TimeSpan.Zero || approval.ApprovedAt > now ||
            approval.ExpiresAt is { } expiry && (expiry.Offset != TimeSpan.Zero || expiry <= now))
            throw new DeskContractException("PERMISSION_DENIED", "Human approval does not match this scope or has expired.");
    }

    public static void ValidateBudget(BudgetPolicy budget)
    {
        if (budget.MaxModelCalls < 1 || budget.MaxSourceCalls < 0 || budget.MaxOutputTokens < 1 || budget.MaxWallSeconds < 1 ||
            budget.PricingBasis is not ("verified_bound" or "unknown" or "subscription_allowance")) throw InvalidBudget();
        if (budget.MonetaryLimit is { } limit)
        {
            if (budget.PricingBasis != "verified_bound" || budget.Currency is not { Length: 3 } currency ||
                currency.Any(c => c is < 'A' or > 'Z') || !decimal.TryParse(CanonicalJson.NormalizeDecimal(limit),
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount < 0) throw InvalidBudget();
        }
        else if (budget.Currency is not null) throw InvalidBudget();
        if (budget.PricingBasis == "unknown" && !budget.UnknownCostAuthorized)
            throw new DeskContractException("PERMISSION_DENIED", "Unknown cost requires explicit approval before dispatch.");
    }

    public static void ValidateDecision(DecisionRevision decision, Guid currentRevision, ApprovalReceipt approval, string scopeSha256, DateTimeOffset now)
    {
        DeskStates.RequireRevision(currentRevision, decision.CaseRevisionId);
        ValidateApproval(approval, scopeSha256, now);
        if (decision.ApprovalId != approval.ApprovalId || decision.ActorId != approval.ActorId)
            throw new DeskContractException("PERMISSION_DENIED", "Decision is not covered by this human approval.");
    }

    private static DeskContractException InvalidBudget() => new("INVALID_INPUT", "Budget limits or pricing basis are invalid.");
}
