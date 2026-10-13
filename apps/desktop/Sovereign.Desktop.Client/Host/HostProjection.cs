using System.Globalization;
using System.Text;
using Sovereign.Desktop.Core;

namespace Sovereign.Desktop.Client.Host;

/// <summary>
/// Maps packet 2.1.0 discriminated host results onto desktop models.
/// Missing fields stay Unknown/Missing. Unsupported <c>type</c> values are not coerced.
/// Decimal evaluation values remain invariant-culture strings; they are never parsed as binary floats.
/// </summary>
public static class HostProjection
{
    public static bool TryType(HostJson result, string expected, out string reason)
    {
        var type = result["type"].AsString();
        if (type is null)
        {
            reason = "result type discriminator missing";
            return false;
        }

        if (!string.Equals(type, expected, StringComparison.Ordinal))
        {
            reason = "unsupported result type " + type;
            return false;
        }

        reason = "";
        return true;
    }

    public static RuntimeIdentity MapRuntime(HostJson result, string fallbackStatus)
    {
        if (!TryType(result, "runtime", out var reason))
        {
            return new RuntimeIdentity(
                "native-host",
                result["protocol"].AsString() ?? HostV2.Protocol,
                BackendType.NativeHost,
                [],
                DateTimeOffset.UtcNow,
                "SCHEMA: " + reason + ". Runtime is not treated as healthy. " + fallbackStatus);
        }

        var bundle = result["bundle_version"].AsString() ?? "unknown-bundle";
        var engine = result["engine_version"].AsString() ?? "unknown-engine";
        var python = result["python_version"].AsString() ?? "unknown-python";
        var profile = result["profile_id"].AsString() ?? "unknown-profile";
        var mode = result["mode"].AsString() ?? "unknown";
        var data = result["data_class"].AsString() ?? "unknown";
        var authority = result["trading_authority"].AsString() ?? "unknown";
        var protocol = result["protocol"].AsString() ?? HostV2.Protocol;
        var caps = new[] { mode, data, "trading_authority=" + authority };
        var status =
            "packet 2.1.0 type=runtime mode=" + mode + " data_class=" + data +
            " trading_authority=" + authority + " bundle=" + bundle + " engine=" + engine +
            " python=" + python + " profile=" + profile + ". Offline synthetic only. Not preview fixtures.";
        return new RuntimeIdentity(bundle + "/" + engine, protocol, BackendType.NativeHost, caps, DateTimeOffset.UtcNow, status);
    }

    public static SystemHealth MapHealth(HostJson result)
    {
        if (!TryType(result, "health", out var reason))
        {
            return SchemaHealth("SCHEMA: " + reason);
        }

        var ready = string.Equals(result["status"].AsString(), "READY", StringComparison.Ordinal);
        var engine = ready
            ? new LabeledState<EngineState>(EngineState.Running, "Engine: running", "health.status=READY")
            : new LabeledState<EngineState>(EngineState.Unknown, "Engine: unknown", "health.status=" + (result["status"].AsString() ?? "missing"));
        var paper = result["paper"].AsString() ?? "unknown";
        var providers = result["providers"].AsString() ?? "unknown";
        var issues = new List<AttentionItem>();
        if (!string.Equals(paper, "UNAVAILABLE", StringComparison.Ordinal))
        {
            issues.Add(new AttentionItem("paper", "warning", "Unexpected paper field: " + paper, "Do not activate paper.", null, null));
        }
        else
        {
            issues.Add(new AttentionItem("paper", "info", "Paper is UNAVAILABLE.", "Do not activate paper accounts.", null, null));
        }

        if (!string.Equals(providers, "UNAVAILABLE", StringComparison.Ordinal))
        {
            issues.Add(new AttentionItem("providers", "warning", "Unexpected providers field: " + providers, "Do not activate providers.", null, null));
        }
        else
        {
            issues.Add(new AttentionItem("providers", "info", "Providers are UNAVAILABLE.", "Do not activate paid models or brokers.", null, null));
        }

        return new SystemHealth(
            engine,
            new LabeledState<ConnectivityState>(ConnectivityState.Unavailable, "Provider: unavailable", "health.providers=" + providers),
            new LabeledState<ConnectivityState>(ConnectivityState.Unavailable, "Broker: unavailable", "health.paper=" + paper),
            new LabeledState<ReconciliationState>(ReconciliationState.Unknown, "Reconciliation: unknown", "reconciliation is not a 2.1.0 health field"),
            new LabeledState<AuthorizationState>(AuthorizationState.None, "Authorization: none", "offline engine trading_authority NONE"),
            Presence<DateTimeOffset>.Unknown("next cycle is not a 2.1.0 health field"),
            issues,
            DateTimeOffset.TryParse(result["checked_at"].AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var checkedAt)
                ? checkedAt
                : DateTimeOffset.UtcNow);
    }

    public static SystemHealth SchemaHealth(string reason) => new(
        new LabeledState<EngineState>(EngineState.Unknown, "Engine: unknown", reason),
        new LabeledState<ConnectivityState>(ConnectivityState.Unknown, "Provider: unknown", reason),
        new LabeledState<ConnectivityState>(ConnectivityState.Unknown, "Broker: unknown", reason),
        new LabeledState<ReconciliationState>(ReconciliationState.Unknown, "Reconciliation: unknown", reason),
        new LabeledState<AuthorizationState>(AuthorizationState.Unknown, "Authorization: unknown", reason),
        Presence<DateTimeOffset>.Unknown(reason),
        [new AttentionItem("schema", "warning", reason, "Do not treat this as a healthy runtime.", null, null)],
        DateTimeOffset.UtcNow);

    public static SystemHealth UnavailableHealth(string reason) => new(
        new LabeledState<EngineState>(EngineState.Unknown, "Engine: unknown", reason),
        new LabeledState<ConnectivityState>(ConnectivityState.Unavailable, "Provider: unavailable", reason),
        new LabeledState<ConnectivityState>(ConnectivityState.Unavailable, "Broker: unavailable", reason),
        new LabeledState<ReconciliationState>(ReconciliationState.Unknown, "Reconciliation: unknown", reason),
        new LabeledState<AuthorizationState>(AuthorizationState.Unknown, "Authorization: unknown", reason),
        Presence<DateTimeOffset>.Unknown(reason),
        [new AttentionItem("unavailable", "warning", reason, "Session remains authenticated. This is a capability state, not a disconnect.", null, null)],
        DateTimeOffset.UtcNow);

    public static Presence<AccountView> MapAccount(HostJson result)
    {
        if (result.IsNull || !result.IsObject)
        {
            return Presence<AccountView>.Unknown("account.get result was empty.");
        }

        if (!TryType(result, "account", out var reason))
        {
            return Presence<AccountView>.Unknown(reason);
        }

        var id = result["run_id"].AsString();
        if (id is null)
        {
            return Presence<AccountView>.Unknown("account.get result lacked run_id.");
        }

        var authority = result["trading_authority"].AsString() ?? "unknown";
        var equity = MapMoney(result, "ending_equity_cents");
        return Presence<AccountView>.Known(new AccountView(
            id,
            result["environment"].AsString() ?? "offline",
            result["balance_basis"].AsString() ?? "host-integer-cents",
            DateTimeOffset.UtcNow,
            MapMoney(result, "cash_cents"),
            MapHoldings(result),
            MapMoney(result, "reserved_cash_cents"),
            result["open_order_ids"].AsArray().Select(x => x.AsString()).Where(x => x is not null).Cast<string>().ToArray(),
            result["incomplete_valuation_reasons"].AsArray().Select(x => x.AsString()).Where(x => x is not null).Cast<string>().ToArray(),
            "native-host type=account",
            equity,
            authority));
    }

    public static IReadOnlyList<DecisionRecord> MapDecisions(HostJson result)
    {
        if (!TryType(result, "decision_list", out _))
        {
            return [];
        }

        var list = ListOf(result, "items", "decisions");
        return list.Select(MapDecision).Where(d => d is not null).Cast<DecisionRecord>().ToArray();
    }

    public static IReadOnlyList<OrderRecord> MapOrders(HostJson result)
    {
        if (!TryType(result, "order_list", out _))
        {
            return [];
        }

        var list = ListOf(result, "items", "orders");
        return list.Select(MapOrder).Where(o => o is not null).Cast<OrderRecord>().ToArray();
    }

    public static IReadOnlyList<ClientEvent> MapEvents(HostJson result, out bool gap, out long? nextCursor, out long firstAvailable)
    {
        gap = false;
        nextCursor = null;
        firstAvailable = 0;
        if (!TryType(result, "event_batch", out _))
        {
            return [];
        }

        gap = result["gap"].AsBool() == true;
        nextCursor = result["next_cursor"].AsInt() ?? result["last_available"].AsInt();
        firstAvailable = result["first_available"].AsInt() ?? 0;
        var events = ListOf(result, "events", "items");
        return events.Select(MapEvent).Where(e => e is not null).Cast<ClientEvent>().ToArray();
    }

    public static IReadOnlyList<CapabilityItem> MapCapabilities(HostJson result)
    {
        if (!TryType(result, "capability_list", out _))
        {
            return [];
        }

        var list = ListOf(result, "items", "capabilities");
        return list.Select(MapCapability).Where(c => c is not null).Cast<CapabilityItem>().ToArray();
    }

    public static EvaluationSummary MapEvaluation(HostJson result)
    {
        if (!TryType(result, "evaluation", out var reason))
        {
            return new EvaluationSummary(
                "unknown",
                Presence<int>.Unknown(reason),
                Presence<string>.Unknown(reason),
                "not mapped",
                "not mapped",
                "not mapped",
                0,
                0,
                0,
                0,
                "schema-error",
                "schema-error",
                "INSUFFICIENT_EVIDENCE",
                reason + ". Evaluation is not treated as healthy campaign data.");
        }

        var value = result["value"];
        var runId = result["run_id"].AsString() ?? "unknown";
        var decimalStrings = ListDecimalStrings(value);
        var actualCash = value["actual_account"]["cash"];
        var performance = actualCash.IsInt
            ? "host value.actual_account.cash integer cents=" + actualCash.Int + "; decimals are JSON strings"
            : "host evaluation value; decimals are invariant-culture JSON strings";
        var countsAbsent = !result["missed_count"].IsInt && !value["missed_count"].IsInt;
        var plot = decimalStrings.Count > 0
            ? "Evaluation value uses invariant-culture decimal strings (" + string.Join(", ", decimalStrings.Take(6)) + "); no plot. Binary floats are not accepted."
            : "Host did not supply a plot.";
        if (countsAbsent)
        {
            plot += " missed/late/abstain/blocked counts were absent; displayed zeros are not host-reported.";
        }

        return new EvaluationSummary(
            runId,
            result["sample_count"].IsInt
                ? Presence<int>.Known((int)result["sample_count"].Int)
                : Presence<int>.Unknown("sample_count absent from evaluation result"),
            value["coverage"].AsString() is string c ? Presence<string>.Known(c) : Presence<string>.Unknown("coverage absent from evaluation.value"),
            performance,
            result["benchmark_assumptions"].AsString() ?? "not supplied",
            result["cost_assumptions"].AsString() ?? "not supplied",
            (int)(result["missed_count"].AsInt() ?? value["missed_count"].AsInt() ?? 0),
            (int)(result["late_count"].AsInt() ?? 0),
            (int)(result["abstain_count"].AsInt() ?? 0),
            (int)(result["blocked_count"].AsInt() ?? 0),
            value["software"].AsString() ?? result["software_result"].AsString() ?? "unknown",
            value["operational_readiness"].AsString() ?? result["operational_readiness"].AsString() ?? "unknown",
            value["investment"].AsString() ?? result["investment_verdict"].AsString() ?? "INSUFFICIENT_EVIDENCE",
            plot);
    }

    public static EvidenceDocument? MapEvidence(HostJson result)
    {
        var id = result["id"].AsString() ?? result["evidence_id"].AsString();
        if (id is null)
        {
            return null;
        }

        return new EvidenceDocument(
            id,
            result["title"].AsString() ?? id,
            result["media_type"].AsString() ?? "text/plain",
            Sovereign.Desktop.Core.Evidence.EvidenceSafety.AsData(result["text"].AsString() ?? ""),
            ["https", "http"],
            result["source_uris"].AsArray().Select(x => x.AsString()).Where(x => x is not null).Cast<string>().ToArray());
    }

    public static EvidenceDocument UnavailableEvidence(string requestedId, HostJson reply)
    {
        var error = reply["error"];
        var result = reply["result"];
        var code = error["code"].AsString() ?? "UNAVAILABLE";
        var message = error["message"].AsString() ?? code;
        var reason = result["reason"].AsString() ?? message;
        var text = Sovereign.Desktop.Core.Evidence.EvidenceSafety.AsData(
            "API-NEGATIVE evidence.get" + Environment.NewLine +
            "status=UNAVAILABLE" + Environment.NewLine +
            "requested_id=" + requestedId + Environment.NewLine +
            "code=" + code + Environment.NewLine +
            "reason=" + reason + Environment.NewLine +
            "This receipt is an API negative test, not the user-facing evidence-selection journey.");
        return new EvidenceDocument(
            requestedId,
            "API-NEGATIVE: UNAVAILABLE",
            "text/plain; x-sovereign-receipt=api-negative-evidence-get",
            text,
            [],
            []);
    }

    public static OperationStatus MapStatus(string? status) => status switch
    {
        "COMPLETED" => OperationStatus.Completed,
        "ACCEPTED" => OperationStatus.Accepted,
        "REJECTED" => OperationStatus.Rejected,
        "TIMED_OUT" => OperationStatus.TimedOut,
        "UNAVAILABLE" => OperationStatus.Unavailable,
        _ => OperationStatus.Unknown
    };

    public static string FailureText(HostJson reply)
    {
        if (reply["error"].IsObject)
        {
            return (reply["error"]["code"].AsString() ?? "ERROR") + ": " + (reply["error"]["message"].AsString() ?? "");
        }

        var result = reply["result"];
        if (result["reason"].AsString() is string reason)
        {
            return reason;
        }

        return reply["status"].AsString() ?? "unknown-status";
    }

    private static IReadOnlyList<HostJson> ListOf(HostJson result, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (result[key].IsArray)
            {
                return result[key].AsArray();
            }
        }

        return result.IsArray ? result.AsArray() : [];
    }

    private static DecisionRecord? MapDecision(HostJson d)
    {
        var id = d["cycle_id"].AsString() ?? d["id"].AsString();
        if (id is null)
        {
            return null;
        }

        var coverage = d["coverage"].AsString();
        var reasons = d["reasons"].IsArray
            ? d["reasons"].AsArray().Select(x => x.AsString()).Where(x => x is not null).Cast<string>().ToArray()
            : coverage is null ? [] : [coverage];
        return new DecisionRecord(
            id,
            ParseAction(d["action"].AsString()),
            d["instrument"].AsString() ?? d["symbol"].AsString() ?? "unknown",
            MapDecimal(d["quantity"], d["qty"]),
            MapMoneyNode(d["price"], d["price_cents"]),
            d["evidence_ids"].AsArray().Select(x => x.AsString()).Where(x => x is not null).Cast<string>().ToArray(),
            d["account_revision"].AsString() ?? d["snapshot_revision"].AsInt()?.ToString() ?? "unknown",
            reasons,
            MapGate(d["admission"], "admission"),
            MapGate(d["risk"], "risk"),
            MapGate(d["execution"], "execution"),
            DateTimeOffset.UtcNow,
            d["related_order_id"].AsString() ?? d["order_id"].AsString());
    }

    private static OrderRecord? MapOrder(HostJson o)
    {
        var id = o["client_order_id"].AsString() ?? o["id"].AsString();
        if (id is null)
        {
            return null;
        }

        return new OrderRecord(
            id,
            o["proposal_id"].AsString(),
            o["decision_id"].AsString(),
            o["symbol"].AsString() ?? o["instrument"].AsString() ?? "unknown",
            ParseOrder(o["requested_state"].AsString() ?? o["status"].AsString() ?? o["side"].AsString()),
            o.Contains("acknowledged_state") ? Presence<OrderLifecycle>.Known(ParseOrder(o["acknowledged_state"].AsString())) : Presence<OrderLifecycle>.Unknown("acknowledged_state absent"),
            o.Contains("current_state") || o.Contains("status")
                ? Presence<OrderLifecycle>.Known(ParseOrder(o["current_state"].AsString() ?? o["status"].AsString()))
                : Presence<OrderLifecycle>.Unknown("current_state absent"),
            [],
            MapDecimal(o["remaining_quantity"], o["qty"], o["filled_qty"]),
            DateTimeOffset.UtcNow,
            o["ambiguity_reasons"].AsArray().Select(x => x.AsString()).Where(x => x is not null).Cast<string>().ToArray());
    }

    private static ClientEvent? MapEvent(HostJson e)
    {
        var seq = e["sequence"].AsInt();
        var id = e["id"].AsString() ?? seq?.ToString();
        if (id is null)
        {
            return null;
        }

        var cursor = seq?.ToString() ?? e["cursor"].AsString() ?? id;
        var kind = e["kind"].AsString() ?? "event";
        var summary = e["summary"].AsString() ?? e["body"]["kind"].AsString() ?? kind;
        return new ClientEvent(
            id,
            cursor,
            DateTimeOffset.TryParse(e["at_utc"].AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.UtcNow,
            kind,
            summary ?? "",
            e["decision_id"].AsString() ?? e["body"]["decision_id"].AsString(),
            e["order_id"].AsString() ?? e["body"]["order_id"].AsString(),
            false);
    }

    private static CapabilityItem? MapCapability(HostJson c)
    {
        var id = c["id"].AsString() ?? c["method"].AsString();
        if (id is null)
        {
            return null;
        }

        var status = c["status"].AsString() ?? "unknown";
        return new CapabilityItem(
            id,
            c["version"].AsString() ?? "v2",
            status,
            c["provider"].AsString() ?? "host",
            c["permission_scope"].AsString() ?? "offline-read",
            c["required_capabilities"].AsArray().Select(x => x.AsString()).Where(x => x is not null).Cast<string>().ToArray(),
            c["reason"].AsString() ?? c["availability_reason"].AsString() ?? "",
            c["cost_status"].AsString() ?? "unknown",
            c["last_outcome"].AsString(),
            c["catalog_status"].AsString() ?? "host");
    }

    private static IReadOnlyList<Holding> MapHoldings(HostJson result)
    {
        if (result["holdings"].IsArray)
        {
            return result["holdings"].AsArray().Select(h => new Holding(
                h["instrument"].AsString() ?? "unknown",
                MapDecimal(h["quantity"], h["qty"]),
                MapMoneyNode(h["mark"], h["mark_cents"]),
                h["quantity_source"].AsString() ?? "host",
                h["incomplete_valuation_reason"].AsString())).ToArray();
        }

        if (result["positions"].IsObject)
        {
            return result["positions"].Keys.Select(sym => new Holding(
                sym,
                result["positions"][sym].IsInt
                    ? Presence<decimal>.Known(result["positions"][sym].Int)
                    : Presence<decimal>.Unknown("position quantity was not an integer"),
                Presence<Money>.Unknown("mark not supplied with positions object"),
                "host-positions",
                null)).ToArray();
        }

        return [];
    }

    private static Presence<Money> MapMoney(HostJson result, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (result[key].IsInt)
            {
                return Presence<Money>.Known(Money.UsdCents(result[key].Int));
            }

            if (result[key].Kind == HostJsonKind.String)
            {
                return Presence<Money>.Unknown(key + " was a string; integer cents are required");
            }

            if (result[key].Kind == HostJsonKind.Null && result.Contains(key))
            {
                return Presence<Money>.Missing(key + " was null");
            }
        }

        return Presence<Money>.Unknown("money field absent from host result (" + string.Join("/", keys) + ")");
    }

    private static Presence<Money> MapMoneyNode(HostJson a, HostJson b)
    {
        if (a.IsInt)
        {
            return Presence<Money>.Known(Money.UsdCents(a.Int));
        }

        if (b.IsInt)
        {
            return Presence<Money>.Known(Money.UsdCents(b.Int));
        }

        if (a.IsObject && a["minor_units"].IsInt)
        {
            return Presence<Money>.Known(new Money(a["currency"].AsString() ?? "USD", a["minor_units"].Int));
        }

        return Presence<Money>.Unknown("price/mark absent");
    }

    private static Presence<decimal> MapDecimal(params HostJson[] nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsInt)
            {
                return Presence<decimal>.Known(node.Int);
            }

            if (node.IsString && decimal.TryParse(node.String, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                && node.String!.IndexOfAny(['e', 'E']) < 0)
            {
                return Presence<decimal>.Known(parsed);
            }
        }

        return Presence<decimal>.Unknown("quantity absent");
    }

    private static GateResult MapGate(HostJson node, string name)
    {
        if (!node.IsObject)
        {
            return new GateResult(name, GateVerdict.Unknown, "gate object absent");
        }

        var verdict = node["verdict"].AsString() ?? node["status"].AsString();
        var parsed = verdict?.ToLowerInvariant() switch
        {
            "pass" => GateVerdict.Pass,
            "fail" => GateVerdict.Fail,
            "blocked" => GateVerdict.Blocked,
            "missing" => GateVerdict.Missing,
            "unsupported" => GateVerdict.Unsupported,
            _ => GateVerdict.Unknown
        };
        return new GateResult(name, parsed, node["reason"].AsString() ?? "no reason");
    }

    private static DecisionAction ParseAction(string? raw) => raw?.ToLowerInvariant() switch
    {
        "buy" => DecisionAction.Buy,
        "hold" => DecisionAction.Hold,
        "reduce" => DecisionAction.Reduce,
        "exit" => DecisionAction.Exit,
        "abstain" => DecisionAction.Abstain,
        "select" => DecisionAction.Buy,
        _ => DecisionAction.Unknown
    };

    private static OrderLifecycle ParseOrder(string? raw) => raw?.ToLowerInvariant() switch
    {
        "requestacceptedlocally" or "request_accepted_locally" or "new" => OrderLifecycle.RequestAcceptedLocally,
        "sendattempted" or "send_attempted" => OrderLifecycle.SendAttempted,
        "brokeracknowledged" or "broker_acknowledged" or "filled" => OrderLifecycle.BrokerAcknowledged,
        "partialfill" or "partial_fill" or "partially_filled" => OrderLifecycle.PartialFill,
        "cancelpending" or "cancel_pending" => OrderLifecycle.CancelPending,
        "canceled" or "cancelled" => OrderLifecycle.Canceled,
        "rejected" => OrderLifecycle.Rejected,
        "buy" or "sell" => OrderLifecycle.RequestAcceptedLocally,
        _ => OrderLifecycle.Unknown
    };

    private static IReadOnlyList<string> ListDecimalStrings(HostJson node)
    {
        var found = new List<string>();
        Walk(node);
        return found;

        void Walk(HostJson value)
        {
            if (value.IsString && LooksLikeDecimalString(value.String!))
            {
                found.Add(value.String!);
                return;
            }

            if (value.IsObject)
            {
                foreach (var key in value.Keys)
                {
                    Walk(value[key]);
                }
            }
            else if (value.IsArray)
            {
                foreach (var item in value.AsArray())
                {
                    Walk(item);
                }
            }
        }
    }

    private static bool LooksLikeDecimalString(string value)
    {
        var dot = false;
        var digits = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '-' && i == 0)
            {
                continue;
            }

            if (ch == '.')
            {
                if (dot)
                {
                    return false;
                }

                dot = true;
                continue;
            }

            if (ch is < '0' or > '9')
            {
                return false;
            }

            digits = true;
        }

        return digits && dot;
    }
}
