"""Generate synthetic desktop fixtures. No private historical packets."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCEN = ROOT / "fixtures" / "desktop" / "scenarios"
ARCH = ROOT / "fixtures" / "desktop" / "archives"
OBS = "2026-09-15T12:00:00Z"


def money(cents, reason=None, presence="known"):
    if presence != "known":
        return {"presence": presence, "reason": reason}
    return {"presence": "known", "currency": "USD", "minor_units": cents}


def qty(value, reason=None, presence="known"):
    if presence != "known":
        return {"presence": presence, "reason": reason}
    return {"presence": "known", "decimal": str(value)}


def text(value, reason=None, presence="known"):
    if presence != "known":
        return {"presence": presence, "reason": reason}
    return {"presence": "known", "text": value}


def enum_known(value):
    return {"presence": "known", "text": value}


def state(value, label, reason=None):
    return {"value": value, "label": label, "reason": reason}


def gate(verdict, reason):
    return {"verdict": verdict, "reason": reason}


def runtime(backend="PreviewFixture"):
    return {
        "build_id": "desktop-preview-0.1.0",
        "protocol_version": "sovereign.host/v1",
        "backend_type": backend,
        "capabilities": ["read-decisions", "read-account", "local-drafts"],
        "observation_timestamp": OBS,
        "integration_status": "Native engine not connected. Preview/archive only.",
    }


def eval_block(**kwargs):
    base = {
        "campaign_id": "preview-campaign",
        "sample_count": {"presence": "known", "int": 3},
        "coverage": {"presence": "known", "text": "3 of 3 cycles recorded"},
        "performance_basis": "journal-derived cents; not a live broker NAV",
        "benchmark_assumptions": "buy-hold same universe, zero borrow",
        "cost_assumptions": "10 bps modeled; not broker-reported",
        "missed_count": 0,
        "late_count": 0,
        "abstain_count": 0,
        "blocked_count": 0,
        "software_result": "desktop-preview-component",
        "operational_readiness": "native-host-not-connected",
        "investment_verdict": "INSUFFICIENT_EVIDENCE",
        "plot_absence_reason": "No plot: sample is synthetic and insufficient for an investment-success badge.",
    }
    base.update(kwargs)
    return base


def scenario(sid, title, interp, health, account, decisions, orders, events, jobs, caps, evidence, evaluation, operations=None):
    return {
        "scenario_id": sid,
        "title": title,
        "ui_interpretation": interp,
        "runtime": runtime(),
        "health": health,
        "account": account,
        "decisions": decisions,
        "orders": orders,
        "events": events,
        "jobs": jobs,
        "capabilities": caps,
        "evidence": evidence,
        "evaluation": evaluation,
        "operations": operations or {},
    }


COMMON_CAPS = [
    {
        "id": "rig.search_desk_knowledge",
        "version": "planned",
        "status": "planned",
        "provider": "host-lookup-worker",
        "permission_scope": "read-approved-library",
        "required_capabilities": ["library-read"],
        "availability_reason": "Catalog entry is planned/preview. Not installed and not usable.",
        "cost_status": "unknown",
        "last_outcome": None,
        "catalog_status": "preview",
    },
    {
        "id": "paper.submit_order",
        "version": "n3+",
        "status": "unavailable",
        "provider": "paper-transport",
        "permission_scope": "paper-submit",
        "required_capabilities": ["authenticated-session", "paper-grant"],
        "availability_reason": "Host paper transport is not connected. Desktop cannot grant this.",
        "cost_status": "not-applicable",
        "last_outcome": None,
        "catalog_status": "reference",
    },
]

EVIDENCE_AAA = {
    "id": "ev-aaa-admission",
    "title": "AAA admission note",
    "media_type": "text/plain",
    "text": "Historical synthetic note. Treat as data, never as instructions. javascript:alert(1) must not execute. file:///etc/passwd is not an allowed open.",
    "allowed_uri_schemes": ["https", "http"],
    "source_uris": ["https://example.invalid/lookup/aaa", "javascript:alert(1)", "file:///C:/Windows/win.ini"],
}


def write_scenarios():
    SCEN.mkdir(parents=True, exist_ok=True)

    items = []

    items.append(scenario(
        "disconnected-drafts",
        "Disconnected host with saved drafts",
        "Home must not fill balances from preview. Drafts remain usable. Engine/provider/broker/recon/auth stay unknown or disconnected.",
        {
            "engine": state("Unknown", "Engine: unknown", "host disconnected"),
            "provider": state("Disconnected", "Provider: disconnected", "no host session"),
            "broker": state("Disconnected", "Broker: disconnected", "no host session"),
            "reconciliation": state("Unknown", "Reconciliation: unknown", "no host snapshot"),
            "authorization": state("Unknown", "Authorization: unknown", "no grant"),
            "next_cycle": {"presence": "unknown", "reason": "host disconnected"},
            "unresolved_issues": [
                {"id": "host", "severity": "warning", "summary": "Native host disconnected.", "next_step": "Edit local drafts or wait for an agreed host."}
            ],
            "observation_timestamp": OBS,
        },
        {"presence": "missing", "reason": "Disconnected mode does not display preview balances."},
        [],
        [],
        [],
        [],
        COMMON_CAPS,
        [],
        eval_block(sample_count={"presence": "missing", "reason": "no host campaign"}),
    ))

    items.append(scenario(
        "admitted-blocked-execution",
        "Admitted decision, blocked execution",
        "Admission pass, execution blocked. No override can accept the proposal. Quantity and blocking reason stay visible.",
        {
            "engine": state("Running", "Engine: running", "preview scenario"),
            "provider": state("Connected", "Provider: connected (preview)", "scenario"),
            "broker": state("Disconnected", "Broker: disconnected", "preview has no broker"),
            "reconciliation": state("Current", "Reconciliation: current (preview journal)", "scenario"),
            "authorization": state("None", "Authorization: none", "preview cannot grant"),
            "next_cycle": {"presence": "known", "text": "2026-09-16T14:30:00Z"},
            "unresolved_issues": [
                {"id": "exec-block", "severity": "warning", "summary": "AAA execution blocked.", "next_step": "Inspect execution gate.", "related_decision_id": "dec-aaa-1"}
            ],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-a",
            "environment": "offline-preview",
            "balance_basis": "journal-derived",
            "observed_at": OBS,
            "cash": money(91100),
            "holdings": [
                {"instrument": "AAA", "quantity": qty("11"), "mark": money(900), "quantity_source": "journal-derived"}
            ],
            "reservations": money(0),
            "open_order_ids": [],
            "incomplete_valuation_reasons": [],
            "source_label": "preview journal",
        },
        [{
            "id": "dec-aaa-1",
            "action": "Buy",
            "instrument": "AAA",
            "quantity": qty("5"),
            "price": money(910),
            "evidence_ids": ["ev-aaa-admission"],
            "account_revision": "rev-7",
            "reasons": ["Admitted on synthetic evidence.", "Execution blocked: no paper grant."],
            "admission": gate("Pass", "Evidence packet hash-consistent in preview."),
            "risk": gate("Pass", "Concentration within draft cap in preview numbers."),
            "execution": gate("Blocked", "No execution authority. Preview cannot override."),
            "observed_at": OBS,
            "related_order_id": None,
        }],
        [],
        [{"id": "evt-1", "cursor": "0001", "at": OBS, "kind": "decision", "summary": "AAA admitted, execution blocked", "decision_id": "dec-aaa-1", "gap_before": False}],
        [{"id": "job-1", "title": "AAA dossier (preview)", "status": "completed", "horizon": "3-12m",
          "thesis": text("Quiet beneficiary of fiscal spending (preview scenario)."),
          "counter_thesis": text("Crowding and multiple expansion."),
          "uncertainty": text("Synthetic; not a real answer."),
          "evidence_refs": ["ev-aaa-admission"], "review_triggers": ["grant expiry"],
          "unavailable_reason": None, "preview_scenario": True}],
        COMMON_CAPS,
        [EVIDENCE_AAA],
        eval_block(blocked_count=1),
    ))

    items.append(scenario(
        "missing-valuation",
        "Missing valuation, holdings remain",
        "Mark is missing; quantity remains visible. Cash missing. Do not coerce to zero.",
        {
            "engine": state("Running", "Engine: running", "preview"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Unknown", "Broker: unknown", "no observation"),
            "reconciliation": state("Incomplete", "Reconciliation: incomplete", "missing marks"),
            "authorization": state("None", "Authorization: none", "preview"),
            "next_cycle": {"presence": "known", "text": "2026-09-16T14:30:00Z"},
            "unresolved_issues": [
                {"id": "marks", "severity": "warning", "summary": "Holdings visible without marks.", "next_step": "Do not size a new order from this view."}
            ],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-b",
            "environment": "offline-preview",
            "balance_basis": "journal-derived; marks incomplete",
            "observed_at": OBS,
            "cash": money(0, "cash mark missing", "missing"),
            "holdings": [
                {"instrument": "BBB", "quantity": qty("40"), "mark": money(0, "quote unavailable", "missing"),
                 "quantity_source": "journal-derived", "incomplete_valuation_reason": "quote unavailable"}
            ],
            "reservations": money(0, "reservation unknown", "unknown"),
            "open_order_ids": [],
            "incomplete_valuation_reasons": ["BBB quote unavailable"],
            "source_label": "journal-derived",
        },
        [{
            "id": "dec-bbb-hold",
            "action": "Hold",
            "instrument": "BBB",
            "quantity": qty("40"),
            "price": money(0, "no mark", "missing"),
            "evidence_ids": ["ev-aaa-admission"],
            "account_revision": "rev-3",
            "reasons": ["HOLD because marks are missing; not an ABSTAIN."],
            "admission": gate("Unknown", "Admission not evaluated without quote."),
            "risk": gate("Unknown", "Risk not evaluated without mark."),
            "execution": gate("Blocked", "Cannot execute without valuation."),
            "observed_at": OBS,
            "related_order_id": None,
        }],
        [],
        [],
        [],
        COMMON_CAPS,
        [EVIDENCE_AAA],
        eval_block(sample_count={"presence": "unknown", "reason": "valuation incomplete"}),
    ))

    items.append(scenario(
        "stale-evidence-blocked",
        "Stale evidence blocked proposal",
        "Proposal blocked on stale evidence. Unknown gates stay unknown.",
        {
            "engine": state("Running", "Engine: running", "preview"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Disconnected", "Broker: disconnected", "preview"),
            "reconciliation": state("Stale", "Reconciliation: stale", "evidence older than freshness"),
            "authorization": state("None", "Authorization: none", "preview"),
            "next_cycle": {"presence": "known", "text": "2026-09-16T14:30:00Z"},
            "unresolved_issues": [{"id": "stale", "severity": "warning", "summary": "CCC evidence stale.", "related_decision_id": "dec-ccc-1"}],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-a",
            "environment": "offline-preview",
            "balance_basis": "journal-derived",
            "observed_at": OBS,
            "cash": money(50000),
            "holdings": [],
            "reservations": money(0),
            "open_order_ids": [],
            "incomplete_valuation_reasons": [],
            "source_label": "journal-derived",
        },
        [{
            "id": "dec-ccc-1",
            "action": "Buy",
            "instrument": "CCC",
            "quantity": qty("10"),
            "price": money(1200),
            "evidence_ids": ["ev-aaa-admission"],
            "account_revision": "rev-4",
            "reasons": ["Blocked: universe evidence older than freshness window."],
            "admission": gate("Fail", "Evidence observed_at outside freshness."),
            "risk": gate("Unsupported", "Risk adapter not present in preview."),
            "execution": gate("Blocked", "Admission failed."),
            "observed_at": OBS,
            "related_order_id": None,
        }],
        [],
        [],
        [],
        COMMON_CAPS,
        [EVIDENCE_AAA],
        eval_block(blocked_count=1),
    ))

    items.append(scenario(
        "hold-and-abstain",
        "Explicit HOLD and separate ABSTAIN",
        "HOLD and ABSTAIN must remain distinct list rows and detail actions.",
        {
            "engine": state("Running", "Engine: running", "preview"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Disconnected", "Broker: disconnected", "preview"),
            "reconciliation": state("Current", "Reconciliation: current (preview)", "preview"),
            "authorization": state("None", "Authorization: none", "preview"),
            "next_cycle": {"presence": "known", "text": "2026-09-16T14:30:00Z"},
            "unresolved_issues": [],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-a",
            "environment": "offline-preview",
            "balance_basis": "journal-derived",
            "observed_at": OBS,
            "cash": money(80000),
            "holdings": [
                {"instrument": "DDD", "quantity": qty("3"), "mark": money(1500), "quantity_source": "journal-derived"}
            ],
            "reservations": money(0),
            "open_order_ids": [],
            "incomplete_valuation_reasons": [],
            "source_label": "journal-derived",
        },
        [
            {
                "id": "dec-ddd-hold",
                "action": "Hold",
                "instrument": "DDD",
                "quantity": qty("3"),
                "price": money(1500),
                "evidence_ids": ["ev-aaa-admission"],
                "account_revision": "rev-5",
                "reasons": ["HOLD: keep existing position. This is not ABSTAIN."],
                "admission": gate("Pass", "Existing position admitted."),
                "risk": gate("Pass", "Within cap."),
                "execution": gate("Blocked", "No change requested."),
                "observed_at": OBS,
                "related_order_id": None,
            },
            {
                "id": "dec-eee-abstain",
                "action": "Abstain",
                "instrument": "EEE",
                "quantity": qty("0", "no quantity because abstain", "missing"),
                "price": money(0, "no price because abstain", "missing"),
                "evidence_ids": ["ev-aaa-admission"],
                "account_revision": "rev-5",
                "reasons": ["ABSTAIN: insufficient thesis quality. This is not HOLD."],
                "admission": gate("Fail", "Evidence does not support a decision."),
                "risk": gate("Unknown", "Not evaluated after abstain."),
                "execution": gate("Blocked", "No order from abstain."),
                "observed_at": OBS,
                "related_order_id": None,
            },
        ],
        [],
        [],
        [],
        COMMON_CAPS,
        [EVIDENCE_AAA],
        eval_block(abstain_count=1),
    ))

    items.append(scenario(
        "unknown-order-lost-response",
        "Unknown order after lost response",
        "Current state unknown. UI must not invent broker acknowledgement.",
        {
            "engine": state("Degraded", "Engine: degraded", "lost response"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Unknown", "Broker: unknown", "lost response"),
            "reconciliation": state("Unknown", "Reconciliation: unknown", "lost response"),
            "authorization": state("Granted", "Authorization: granted (preview label only)", "scenario; not actual authority"),
            "next_cycle": {"presence": "unknown", "reason": "clock wait after timeout"},
            "unresolved_issues": [
                {"id": "ord-unknown", "severity": "error", "summary": "Order ORD-9 is unknown after timeout.", "related_order_id": "ord-9"}
            ],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-a",
            "environment": "offline-preview",
            "balance_basis": "journal-derived",
            "observed_at": OBS,
            "cash": money(70000),
            "holdings": [],
            "reservations": money(9100, "reservation still open", "known"),
            "open_order_ids": ["ord-9"],
            "incomplete_valuation_reasons": [],
            "source_label": "journal-derived",
        },
        [{
            "id": "dec-fff-1",
            "action": "Buy",
            "instrument": "FFF",
            "quantity": qty("10"),
            "price": money(910),
            "evidence_ids": ["ev-aaa-admission"],
            "account_revision": "rev-9",
            "reasons": ["Submitted in scenario; response lost."],
            "admission": gate("Pass", "Admitted."),
            "risk": gate("Pass", "Sized."),
            "execution": gate("Pass", "Send attempted."),
            "observed_at": OBS,
            "related_order_id": "ord-9",
        }],
        [{
            "id": "ord-9",
            "proposal_id": "prop-9",
            "decision_id": "dec-fff-1",
            "instrument": "FFF",
            "requested_state": "SendAttempted",
            "acknowledged_state": {"presence": "unknown", "reason": "broker response lost"},
            "current_state": {"presence": "unknown", "reason": "timed out after send; not canceled"},
            "fills": [],
            "remaining_quantity": qty("10"),
            "requested_at": OBS,
            "ambiguity_reasons": ["TIMEOUT_AFTER_SEND", "UNKNOWN_UNTIL_RECOVERY"],
        }],
        [
            {"id": "evt-send", "cursor": "0001", "at": OBS, "kind": "order", "summary": "Send attempted", "order_id": "ord-9", "gap_before": False},
            {"id": "evt-timeout", "cursor": "0003", "at": OBS, "kind": "order", "summary": "Timeout; state unknown", "order_id": "ord-9", "gap_before": True},
        ],
        [],
        COMMON_CAPS,
        [EVIDENCE_AAA],
        eval_block(),
        operations={
            "cancel-order": {
                "operation": "cancel-order",
                "idempotency_key": "preset",
                "status": "TimedOut",
                "host_request_id": "req-lost",
                "detail": "Timed out after send. Status remains UNKNOWN. Local cancel does not manufacture acknowledgement.",
                "simulated_preview_acknowledgement": True,
            }
        },
    ))

    items.append(scenario(
        "partial-fill-cancel-pending",
        "Partial fill, cancel pending",
        "Show remaining quantity, partial fills, and cancel pending without claiming canceled.",
        {
            "engine": state("Running", "Engine: running", "preview"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Connected", "Broker: connected (preview)", "preview"),
            "reconciliation": state("Current", "Reconciliation: current (preview)", "preview"),
            "authorization": state("Paused", "Authorization: paused", "cancel pending"),
            "next_cycle": {"presence": "known", "text": "2026-09-16T14:30:00Z"},
            "unresolved_issues": [
                {"id": "cancel-pending", "severity": "warning", "summary": "Cancel pending on ORD-2.", "related_order_id": "ord-2"}
            ],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-a",
            "environment": "offline-preview",
            "balance_basis": "journal-derived plus broker-observed fills (labeled)",
            "observed_at": OBS,
            "cash": money(60000),
            "holdings": [
                {"instrument": "GGG", "quantity": qty("4"), "mark": money(800), "quantity_source": "broker-observed fill"}
            ],
            "reservations": money(4800),
            "open_order_ids": ["ord-2"],
            "incomplete_valuation_reasons": [],
            "source_label": "mixed journal/broker-observed",
        },
        [{
            "id": "dec-ggg-1",
            "action": "Buy",
            "instrument": "GGG",
            "quantity": qty("10"),
            "price": money(800),
            "evidence_ids": ["ev-aaa-admission"],
            "account_revision": "rev-2",
            "reasons": ["Working order."],
            "admission": gate("Pass", "Admitted."),
            "risk": gate("Pass", "Sized."),
            "execution": gate("Pass", "Acknowledged."),
            "observed_at": OBS,
            "related_order_id": "ord-2",
        }],
        [{
            "id": "ord-2",
            "proposal_id": "prop-2",
            "decision_id": "dec-ggg-1",
            "instrument": "GGG",
            "requested_state": "CancelPending",
            "acknowledged_state": enum_known("PartialFill"),
            "current_state": enum_known("CancelPending"),
            "fills": [
                {"id": "fill-1", "quantity": qty("4"), "price": money(800), "at": OBS}
            ],
            "remaining_quantity": qty("6"),
            "requested_at": OBS,
            "ambiguity_reasons": [],
        }],
        [],
        [],
        COMMON_CAPS,
        [EVIDENCE_AAA],
        eval_block(),
    ))

    items.append(scenario(
        "recon-discrepancy-paused",
        "Reconciliation discrepancy, paused session",
        "Discrepancy is a first-class state. Pause is not a successful stop acknowledgement.",
        {
            "engine": state("Paused", "Engine: paused", "operator pause requested in scenario"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Connected", "Broker: connected (preview)", "preview"),
            "reconciliation": state("Discrepancy", "Reconciliation: discrepancy", "journal cash != broker cash"),
            "authorization": state("Paused", "Authorization: paused", "pause requested; completion unknown until host"),
            "next_cycle": {"presence": "missing", "reason": "session paused"},
            "unresolved_issues": [
                {"id": "recon", "severity": "error", "summary": "Cash discrepancy 1500 cents.", "next_step": "Inspect orders/events. Do not activate."}
            ],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-a",
            "environment": "offline-preview",
            "balance_basis": "journal-derived vs broker-observed mismatch",
            "observed_at": OBS,
            "cash": money(90000),
            "holdings": [],
            "reservations": money(0),
            "open_order_ids": [],
            "incomplete_valuation_reasons": ["broker cash 91500 cents vs journal 90000"],
            "source_label": "journal-derived",
        },
        [],
        [],
        [],
        [],
        COMMON_CAPS,
        [],
        eval_block(),
        operations={
            "PAUSE_NEW_RISK": {
                "operation": "PAUSE_NEW_RISK",
                "status": "Unknown",
                "detail": "Preview pause control shows simulated interaction only. Not actual authority.",
                "simulated_preview_acknowledgement": True,
            }
        },
    ))

    items.append(scenario(
        "expired-grant-unavailable-provider",
        "Expired grant, unavailable provider",
        "Authorization expired. Provider unavailable. Do not show green connected.",
        {
            "engine": state("Stopped", "Engine: stopped", "grant expired"),
            "provider": state("Unavailable", "Provider: unavailable", "model endpoint not configured"),
            "broker": state("Disconnected", "Broker: disconnected", "no grant"),
            "reconciliation": state("Unknown", "Reconciliation: unknown", "engine stopped"),
            "authorization": state("Expired", "Authorization: expired", "grant ended 2026-09-01"),
            "next_cycle": {"presence": "missing", "reason": "expired grant"},
            "unresolved_issues": [
                {"id": "grant", "severity": "warning", "summary": "Grant expired.", "next_step": "Create a new session draft. Desktop cannot activate it."}
            ],
            "observation_timestamp": OBS,
        },
        {"presence": "unknown", "reason": "Grant expired; account view not authorized."},
        [],
        [],
        [],
        [{
            "id": "job-blocked",
            "title": "User question cannot start",
            "status": "unavailable",
            "horizon": "3-12m",
            "thesis": {"presence": "missing", "reason": "job never started"},
            "counter_thesis": {"presence": "missing", "reason": "job never started"},
            "uncertainty": {"presence": "missing", "reason": "job never started"},
            "evidence_refs": [],
            "review_triggers": [],
            "unavailable_reason": "Provider unavailable and grant expired. Preview will not invent an answer.",
            "preview_scenario": True,
        }],
        COMMON_CAPS,
        [],
        eval_block(sample_count={"presence": "missing", "reason": "expired grant"}),
    ))

    items.append(scenario(
        "long-running-job-cancel-timeout",
        "Long-running job, cancel, timeout",
        "Cancellation request is local until acknowledged. Timeout stays unknown.",
        {
            "engine": state("Running", "Engine: running", "preview"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Disconnected", "Broker: disconnected", "preview"),
            "reconciliation": state("Current", "Reconciliation: current (preview)", "preview"),
            "authorization": state("None", "Authorization: none", "preview"),
            "next_cycle": {"presence": "known", "text": "2026-09-16T14:30:00Z"},
            "unresolved_issues": [
                {"id": "job-timeout", "severity": "warning", "summary": "Job cancel not acknowledged.", "next_step": "Treat status as unknown."}
            ],
            "observation_timestamp": OBS,
        },
        {"presence": "missing", "reason": "Lookup workspace scenario does not imply an account."},
        [],
        [],
        [
            {"id": "evt-run", "cursor": "0001", "at": OBS, "kind": "job", "summary": "Job running", "gap_before": False},
            {"id": "evt-cancel-req", "cursor": "0002", "at": OBS, "kind": "job", "summary": "Cancel requested locally", "gap_before": False},
            {"id": "evt-timeout", "cursor": "0003", "at": OBS, "kind": "job", "summary": "Timeout; acknowledgement unknown", "gap_before": False},
        ],
        [{
            "id": "job-long",
            "title": "Long-running preview job",
            "status": "unknown",
            "horizon": "0-3m",
            "thesis": {"presence": "unknown", "reason": "job not completed"},
            "counter_thesis": {"presence": "unknown", "reason": "job not completed"},
            "uncertainty": {"presence": "unknown", "reason": "job not completed"},
            "evidence_refs": [],
            "review_triggers": [],
            "unavailable_reason": "Cancel requested; host acknowledgement not received. Timeout remains UNKNOWN.",
            "preview_scenario": True,
        }],
        COMMON_CAPS,
        [],
        eval_block(),
        operations={
            "cancel-job": {
                "operation": "cancel-job",
                "status": "TimedOut",
                "detail": "Local cancellation of the wait does not mean the backend canceled. Status UNKNOWN.",
                "simulated_preview_acknowledgement": True,
            }
        },
    ))

    items.append(scenario(
        "missed-late-insufficient-eval",
        "Missed/late cycle, insufficient evaluation",
        "Missed and late are distinct. Investment verdict remains INSUFFICIENT_EVIDENCE. No success badge.",
        {
            "engine": state("Running", "Engine: running", "preview"),
            "provider": state("Connected", "Provider: connected (preview)", "preview"),
            "broker": state("Disconnected", "Broker: disconnected", "preview"),
            "reconciliation": state("Stale", "Reconciliation: stale", "missed cycle"),
            "authorization": state("None", "Authorization: none", "preview"),
            "next_cycle": {"presence": "known", "text": "2026-09-16T14:30:00Z"},
            "unresolved_issues": [
                {"id": "missed", "severity": "warning", "summary": "One missed and one late cycle."}
            ],
            "observation_timestamp": OBS,
        },
        {
            "presence": "known",
            "account_id": "preview-paper-a",
            "environment": "offline-preview",
            "balance_basis": "journal-derived",
            "observed_at": OBS,
            "cash": money(91100),
            "holdings": [],
            "reservations": money(0),
            "open_order_ids": [],
            "incomplete_valuation_reasons": [],
            "source_label": "journal-derived",
        },
        [],
        [],
        [
            {"id": "evt-missed", "cursor": "0001", "at": OBS, "kind": "cycle", "summary": "Cycle MISSED", "gap_before": False},
            {"id": "evt-late", "cursor": "0002", "at": OBS, "kind": "cycle", "summary": "Cycle LATE", "gap_before": False},
        ],
        [],
        COMMON_CAPS,
        [],
        eval_block(
            sample_count={"presence": "known", "int": 2},
            coverage={"presence": "known", "text": "missed=1 late=1 on-time=0"},
            missed_count=1,
            late_count=1,
            investment_verdict="INSUFFICIENT_EVIDENCE",
        ),
    ))

    for item in items:
        path = SCEN / f"{item['scenario_id']}.json"
        path.write_text(json.dumps(item, indent=2) + "\n", encoding="utf-8")
        print("wrote", path)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_archives():
    good = ARCH / "valid-hash-consistent"
    good.mkdir(parents=True, exist_ok=True)
    note = good / "notes" / "admission.txt"
    note.parent.mkdir(parents=True, exist_ok=True)
    note.write_text("Synthetic historical admission note. Data only.\n", encoding="utf-8")
    snap = (SCEN / "admitted-blocked-execution.json").read_text(encoding="utf-8")
    snapshot = good / "snapshot.json"
    snapshot.write_text(snap, encoding="utf-8")
    files = [
        {"path": "notes/admission.txt", "sha256": sha256(note)},
        {"path": "snapshot.json", "sha256": sha256(snapshot)},
    ]
    packet = {
        "schema_version": "desktop-archive/v1",
        "packet_type": "historical-evidence",
        "observation_window": {"start": "2026-08-01T00:00:00Z", "end": "2026-09-01T00:00:00Z"},
        "declared_files": files,
        "result_references": ["dec-aaa-1"],
    }
    (good / "packet.json").write_text(json.dumps(packet, indent=2) + "\n", encoding="utf-8")

    tampered = ARCH / "tampered-hash"
    tampered.mkdir(parents=True, exist_ok=True)
    tnote = tampered / "notes" / "admission.txt"
    tnote.parent.mkdir(parents=True, exist_ok=True)
    tnote.write_text("TAMPERED\n", encoding="utf-8")
    (tampered / "snapshot.json").write_text(snap, encoding="utf-8")
    bad_packet = dict(packet)
    bad_packet["declared_files"] = [
        {"path": "notes/admission.txt", "sha256": files[0]["sha256"]},
        {"path": "snapshot.json", "sha256": sha256(tampered / "snapshot.json")},
    ]
    (tampered / "packet.json").write_text(json.dumps(bad_packet, indent=2) + "\n", encoding="utf-8")

    unsupported = ARCH / "unsupported-schema"
    unsupported.mkdir(parents=True, exist_ok=True)
    (unsupported / "packet.json").write_text(json.dumps({
        "schema_version": "desktop-archive/v9",
        "packet_type": "historical-evidence",
        "declared_files": [{"path": "notes/admission.txt", "sha256": "00" * 32}],
    }, indent=2) + "\n", encoding="utf-8")

    escape = ARCH / "path-escape"
    escape.mkdir(parents=True, exist_ok=True)
    (escape / "packet.json").write_text(json.dumps({
        "schema_version": "desktop-archive/v1",
        "packet_type": "historical-evidence",
        "declared_files": [{"path": "../secret.txt", "sha256": "00" * 32}],
        "observation_window": {"start": OBS, "end": OBS},
    }, indent=2) + "\n", encoding="utf-8")

    print("archives written")


if __name__ == "__main__":
    write_scenarios()
    write_archives()
