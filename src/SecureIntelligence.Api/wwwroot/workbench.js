"use strict";

const $ = (selector) => document.querySelector(selector);
const state = { key: "", role: "", capabilities: null, request: null, requestId: null, proposals: [], cases: [], view: "diagnosis" };
const titles = { diagnosis: "Diagnosis", knowledge: "Knowledge", reviews: "Learning reviews", cases: "Approved cases" };
function element(tag, className = "", text = "") {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}
function notice(message, error = false) {
  const node = $("#notice");
  node.textContent = message;
  node.className = error ? "error" : "";
  node.hidden = false;
}
async function api(path, payload = null, method = "GET", key = state.key) {
  if (!key) throw new Error("Connect with an API key first.");
  const response = await fetch(`/api/v1${path}`, {
    method, headers: { "X-Internal-Api-Key": key, ...(payload === null ? {} : { "Content-Type": "application/json" }) },
    body: payload === null ? undefined : JSON.stringify(payload), cache: "no-store", credentials: "omit", redirect: "error"
  });
  const text = await response.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch { throw new Error(`Unexpected service response (${response.status}).`); } }
  if (!response.ok) {
    const defaults = { 401: "The API key was not accepted.", 403: "This action requires reviewer access.", 413: "This request is too large.", 429: "Request limit reached. Wait a minute and try again.", 503: "The service or storage is unavailable." };
    const validation = data?.errors ? Object.values(data.errors).flat().join(" ") : null;
    throw new Error(validation || data?.detail || defaults[response.status] || `Request failed (${response.status}).`);
  }
  return data;
}
async function perform(form, action) {
  const buttons = [...form.querySelectorAll("button")];
  buttons.forEach(button => { button.disabled = true; });
  try { await action(); } catch (error) { notice(error.message || "Unable to complete the request.", true); }
  finally { buttons.forEach(button => { button.disabled = false; }); }
}
function empty(container, heading, message) {
  const box = element("div", "panel empty-state");
  box.append(element("h2", "", heading), element("p", "", message));
  container.replaceChildren(box);
}
function date(value) { return new Date(value).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }); }
function table(rows, headings) {
  const result = element("table", "signals-table");
  const head = element("thead"); const heading = element("tr");
  headings.forEach(text => heading.append(element("th", "", text))); head.append(heading);
  const body = element("tbody");
  rows.forEach(values => { const row = element("tr"); values.forEach(value => row.append(element("td", "", String(value ?? "Not supplied")))); body.append(row); });
  result.append(head, body); return result;
}
function field(label, value, multiline = false) {
  const wrapper = element("label", "", label);
  const input = element(multiline ? "textarea" : "input");
  input.value = value; input.required = true; input.maxLength = multiline ? 4000 : 2000;
  if (multiline) input.rows = 3;
  wrapper.append(input); return { wrapper, input };
}
function checkbox(label) {
  const wrapper = element("label", "check-label");
  const input = element("input"); input.type = "checkbox"; input.required = true;
  wrapper.append(input, document.createTextNode(label)); return { wrapper, input };
}
function sourceLine(hit) {
  const row = element("p", "source-line", `Source: ${hit.source}`);
  if (hit.sourceUrl) {
    try {
      const url = new URL(hit.sourceUrl);
      if (url.protocol === "https:" && !url.username && !url.password && !url.search && !url.hash) {
        const link = element("a", "", "Open source"); link.href = url.href; link.target = "_blank"; link.rel = "noopener noreferrer";
        row.append(document.createTextNode(" · "), link);
      }
    } catch { /* A source reference is still shown without a link. */ }
  }
  return row;
}
function hitCard(hit, compact = false) {
  const card = element("article", compact ? "finding" : "panel");
  card.append(element("h3", "", hit.title), sourceLine(hit), element("p", "passage", hit.passage),
    element("p", "source-line", `Matched terms: ${hit.matchedTerms.join(", ")}`));
  return card;
}
function applyAccess() {
  const reviewer = state.role === "reviewer";
  document.querySelectorAll("[data-reviewer]").forEach(node => { node.hidden = !reviewer; });
  $("#review-access").hidden = reviewer; $("#case-access").hidden = reviewer;
  $("#connect-form").hidden = !!state.key; $("#disconnect").hidden = !state.key;
  $("#access-status").textContent = state.key ? (reviewer ? "Reviewer access" : "Caller access") : "Not connected";
  $("#access-status").classList.toggle("connected", !!state.key);
  $("#connection-heading").textContent = state.key ? "Connected to this service" : "Connect to your service";
  $("#connection-note").textContent = state.key
    ? (state.capabilities.learningEnabled ? "Your key stays in memory until you disconnect or reload this page." : "Learning is disabled on this service. Diagnosis and guide search remain available.")
    : "Use a caller key to diagnose and search, or a reviewer key to manage learning.";
}
async function showView(view) {
  state.view = view;
  document.querySelectorAll(".view").forEach(node => { node.hidden = node.id !== `view-${view}`; });
  document.querySelectorAll("[data-view]").forEach(button => {
    button.classList.toggle("active", button.dataset.view === view);
    if (button.dataset.view === view) button.setAttribute("aria-current", "page"); else button.removeAttribute("aria-current");
  });
  $("#page-title").textContent = titles[view]; $("#notice").hidden = true;
  try {
    if (view === "knowledge" && state.key) await loadDocuments();
    if (view === "reviews" && state.role === "reviewer") await loadReviews();
    if (view === "cases" && state.role === "reviewer") await loadCases();
  } catch (error) { notice(error.message, true); }
}
document.querySelectorAll("[data-view]").forEach(button => button.addEventListener("click", () => showView(button.dataset.view)));
$("#connect-form").addEventListener("submit", event => {
  event.preventDefault();
  perform(event.currentTarget, async () => {
    const candidate = $("#api-key").value.trim();
    const capabilities = await api("/capabilities", null, "GET", candidate);
    state.key = candidate; state.role = capabilities.accessLevel; state.capabilities = capabilities;
    $("#api-key").value = ""; applyAccess(); await showView(state.view);
    notice(`Connected with ${state.role === "reviewer" ? "reviewer" : "caller"} access.`);
  });
});
$("#disconnect").addEventListener("click", () => {
  state.key = ""; state.role = ""; state.capabilities = null; state.request = null; state.requestId = null; state.proposals = []; state.cases = [];
  document.querySelectorAll("form").forEach(form => form.reset());
  ["#search-results", "#documents-list", "#reviews-list", "#cases-list"].forEach(selector => $(selector).replaceChildren());
  $("#proposal-form").hidden = true;
  $("#diagnostic-results").classList.add("empty-state");
  $("#diagnostic-results").replaceChildren(element("h2", "", "Your findings will appear here"), element("p", "", "Connect and submit a diagnostic scenario."));
  applyAccess(); notice("Disconnected. Session data has been cleared.");
});
$("#sample-diagnosis").addEventListener("click", () => {
  $("#diag-application").value = "LineDesigner"; $("#diag-issue").value = "ReadyForQuote";
  $("#diag-description").value = "Synthetic test: ready line with incomplete systemization and missing BOM items";
  $("#signal-ready").value = "true"; $("#signal-systemization").value = "false";
  $("#signal-bom").value = "0"; $("#signal-latency").value = "2200";
  notice("Synthetic scenario loaded. Run diagnosis to evaluate it.");
});
$("#diagnose-form").addEventListener("submit", event => {
  event.preventDefault();
  perform(event.currentTarget, async () => {
    const signals = {};
    for (const [selector, key] of [["#signal-ready", "readyForQuote"], ["#signal-systemization", "systemizationConfigured"], ["#signal-bom", "bomItemCount"], ["#signal-latency", "dbLatencyMs"]]) {
      if ($(selector).value !== "") signals[key] = $(selector).value;
    }
    const request = { application: $("#diag-application").value, issueType: $("#diag-issue").value, description: $("#diag-description").value, signals };
    state.request = null; state.requestId = null; $("#proposal-form").hidden = true;
    const result = await api("/diagnose", request, "POST");
    state.request = request; state.requestId = result.requestId;
    renderDiagnosis(result);
    $("#proposal-form").hidden = !state.capabilities.learningEnabled;
    $("#notice").hidden = true;
  });
});
function renderDiagnosis(result) {
  const container = $("#diagnostic-results"); container.classList.remove("empty-state"); container.replaceChildren();
  const heading = element("div", "panel-heading");
  heading.append(element("h2", "", `${result.findings.length} rule finding${result.findings.length === 1 ? "" : "s"}`), element("span", "muted", date(result.evaluatedAtUtc)));
  container.append(heading);
  if (!result.findings.length) container.append(element("p", "muted", "No supported rule matched these signals. This does not establish that the system is healthy."));
  result.findings.forEach(finding => {
    const card = element("article", "finding");
    card.append(element("span", `finding-label ${finding.severity}`, `${finding.severity.toUpperCase()} · ${finding.code}`),
      element("h3", "", finding.summary), element("p", "", finding.explanation));
    if (finding.suggestedAction) card.append(element("p", "", finding.suggestedAction));
    container.append(card);
  });
  const matches = element("section", "result-section"); matches.append(element("h3", "", "Similar approved cases"));
  if (!result.similarCases.length) matches.append(element("p", "muted", "No approved case has sufficient matching evidence."));
  result.similarCases.forEach(match => {
    const card = element("article", "finding");
    card.append(element("h3", "", match.confirmedCause), element("p", "case-resolution", match.resolution),
      element("p", "source-line", `Evidence similarity: ${Math.round(match.similarity * 100)}% · Retrieval score, not probability`));
    if (match.evidence?.length) card.append(table(match.evidence.map(e => [e.signal, e.requestedValue, e.caseValue, e.relation]), ["Signal", "Requested", "Stored", "Comparison"]));
    card.append(element("p", "outcome-summary", `${match.verifiedSuccesses} verified resolved · ${match.verifiedFailures} verified unresolved`));
    if (state.role === "reviewer") {
      const button = element("button", "text-button", "Manage case and record outcome"); button.type = "button";
      button.addEventListener("click", async () => { await showView("cases"); document.querySelector(`[data-case-id="${match.caseId}"]`)?.scrollIntoView({ block: "start" }); }); card.append(button);
    }
    matches.append(card);
  });
  container.append(matches);
  const knowledge = element("section", "result-section"); knowledge.append(element("h3", "", "Relevant guide passages"));
  if (!result.knowledge?.length) knowledge.append(element("p", "muted", "No published guide matched. Publish a reviewed guide in Knowledge to make it searchable."));
  else result.knowledge.forEach(hit => knowledge.append(hitCard(hit, true)));
  container.append(knowledge);
}
$("#proposal-form").addEventListener("submit", event => {
  event.preventDefault();
  perform(event.currentTarget, async () => {
    if (!state.request) throw new Error("Run a diagnosis before proposing a case.");
    const result = await api("/proposals", { request: state.request, confirmedCause: $("#proposal-cause").value, resolution: $("#proposal-resolution").value }, "POST");
    notice(`Case queued for review (${result.proposalId.slice(0, 8)}). It is not available to diagnoses yet.`);
    $("#proposal-form").reset();
  });
});
$("#search-form").addEventListener("submit", event => {
  event.preventDefault();
  perform(event.currentTarget, async () => {
    const hits = await api("/knowledge/search", { application: $("#search-application").value, issueType: $("#search-issue").value || null, query: $("#search-query").value, limit: 5 }, "POST");
    $("#search-results").replaceChildren();
    if (!hits.length) empty($("#search-results"), "No matching passages", "Try another keyword or publish a guide for this application and issue.");
    else hits.forEach(hit => $("#search-results").append(hitCard(hit)));
  });
});
$("#sample-guide").addEventListener("click", () => {
  $("#doc-application").value = "LineDesigner"; $("#doc-issue").value = "ReadyForQuote";
  $("#doc-title").value = "Starter guide: Ready-for-Quote diagnostic checks";
  $("#doc-source").value = "SecureIntelligence ExampleRules.cs · example guide"; $("#doc-url").value = "";
  $("#doc-content").value = "Ready-for-Quote diagnostic checks\n\nIf a Line Designer line is ready for quotation and its BOM item count is zero, inspect the owning application's BOM validation and generation workflow.\n\nIf systemization is not configured, configure it and run the application's normal validation again.\n\nThese checks explain the starter rules. Confirm the workflow against your application's approved documentation before using it in operations.";
  $("#doc-approved").checked = false;
  notice("Example guide loaded. Review it before approving publication.");
});
$("#doc-file").addEventListener("change", async event => {
  const file = event.target.files?.[0]; if (!file) return;
  try {
    if (!/\.(txt|md)$/i.test(file.name) || file.size > 60000) throw new Error("Choose a .txt or .md file under 60 KB.");
    const text = await file.text(); if (text.length > 20000) throw new Error("Guide text cannot exceed 20,000 characters.");
    $("#doc-content").value = text; $("#doc-source").value = file.name; $("#doc-approved").checked = false;
    notice("Guide text loaded. Review it before publishing.");
  } catch (error) { notice(error.message, true); }
});
$("#import-form").addEventListener("submit", event => {
  event.preventDefault();
  perform(event.currentTarget, async () => {
    await api("/knowledge/documents", { application: $("#doc-application").value, issueType: $("#doc-issue").value,
      title: $("#doc-title").value, source: $("#doc-source").value, sourceUrl: $("#doc-url").value || null,
      content: $("#doc-content").value, approvedForPublication: $("#doc-approved").checked }, "POST");
    $("#import-form").reset(); await loadDocuments(); notice("Reviewed guide published. It is now available to searches and diagnoses.");
  });
});
async function loadDocuments() {
  const documents = await api("/knowledge/documents"); const container = $("#documents-list"); container.replaceChildren();
  if (!documents.length) { empty(container, "No published guides yet", "A reviewer can publish an approved guide above. Searches return source passages once guides are available."); return; }
  documents.forEach(document => {
    const card = element("article", "panel"); const title = element("div", "card-title");
    title.append(element("h3", "", document.title), element("span", "muted", `${document.application} · ${document.issueType}`));
    card.append(title, sourceLine(document), element("p", "source-line", `Published ${date(document.publishedAtUtc)}`));
    const actions = element("div", "row-actions"); const view = element("button", "button", "Read guide"); view.type = "button";
    view.addEventListener("click", () => perform(actions, async () => {
      const full = await api(`/knowledge/documents/${encodeURIComponent(document.documentId)}`);
      const existing = card.querySelector(".passage"); if (existing) existing.remove(); else card.insertBefore(element("p", "passage", full.content), actions);
    })); actions.append(view);
    if (state.role === "reviewer") {
      const remove = element("button", "button danger", "Delete"); remove.type = "button";
      remove.addEventListener("click", () => { if (confirm(`Delete the published guide “${document.title}”?`)) perform(actions, async () => {
        await api(`/knowledge/documents/${encodeURIComponent(document.documentId)}`, null, "DELETE"); await loadDocuments(); $("#search-results").replaceChildren(); notice("Guide deleted. It is no longer searchable.");
      }); }); actions.append(remove);
    }
    card.append(actions); container.append(card);
  });
}
async function loadReviews() { state.proposals = await api("/proposals"); renderReviews(); }
function renderReviews() {
  const filter = $("#review-filter").value; const proposals = state.proposals.filter(p => filter === "All" || p.status === filter);
  const container = $("#reviews-list"); container.replaceChildren();
  if (!proposals.length) { empty(container, "No proposals in this view", filter === "Pending" ? "New proposals will appear here for review before they become active cases." : "Select another status to inspect the queue."); return; }
  proposals.forEach(proposal => {
    const card = element("article", "panel"); const heading = element("div", "card-title");
    heading.append(element("h3", "", proposal.case.confirmedCause), element("span", "review-status", proposal.status));
    card.append(heading, element("p", "source-line", `${proposal.case.application} · ${proposal.case.issueType} · Submitted ${date(proposal.createdAtUtc)}`),
      table(Object.entries(proposal.case.signals), ["Signal", "Supplied value"]));
    if (proposal.status !== "Pending") {
      card.append(element("p", "case-resolution", proposal.case.resolution), element("p", "source-line", `Reviewed ${date(proposal.reviewedAtUtc)}${proposal.rejectionReason ? ` · ${proposal.rejectionReason}` : ""}`));
    } else {
      const form = element("form", "review-fields"); const cause = field("Reviewed cause", proposal.case.confirmedCause); const resolution = field("Reviewed resolution", proposal.case.resolution, true);
      const approval = checkbox("I verified the cause and resolution and approve this case for learning.");
      const actions = element("div", "row-actions"); const approve = element("button", "button primary", "Approve case"); approve.type = "submit";
      const reason = element("select"); reason.setAttribute("aria-label", "Rejection reason");
      for (const [value, label] of [["InsufficientEvidence", "Insufficient evidence"], ["IncorrectCause", "Incorrect cause"], ["Duplicate", "Duplicate"], ["Other", "Other"]]) { const option = element("option", "", label); option.value = value; reason.append(option); }
      const rejectLabel = element("label", "inline-label", "Reject reason"); rejectLabel.append(reason);
      const reject = element("button", "button", "Reject"); reject.type = "button";
      actions.append(approve, rejectLabel, reject); form.append(cause.wrapper, resolution.wrapper, approval.wrapper, actions);
      form.addEventListener("submit", event => { event.preventDefault(); perform(form, async () => {
        await api(`/proposals/${encodeURIComponent(proposal.proposalId)}/approve`, { approvedForLearning: approval.input.checked, confirmedCause: cause.input.value, resolution: resolution.input.value }, "POST");
        await loadReviews(); notice("Case approved and published. Future matching diagnoses can retrieve it.");
      }); });
      reject.addEventListener("click", () => perform(form, async () => { await api(`/proposals/${encodeURIComponent(proposal.proposalId)}/reject`, { reason: reason.value }, "POST"); await loadReviews(); notice("Proposal rejected. It was not added to active cases."); }));
      card.append(form);
    }
    const remove = element("button", "text-button", "Delete review record"); remove.type = "button";
    remove.addEventListener("click", () => { if (confirm("Delete this review record? Any already-approved case remains active; pending cases will not be published.")) perform(card, async () => {
      await api(`/proposals/${encodeURIComponent(proposal.proposalId)}`, null, "DELETE"); await loadReviews(); notice("Review record removed. Any approved case remains active.");
    }); });
    const removal = element("div", "row-actions"); removal.append(remove); card.append(removal);
    container.append(card);
  });
}
async function loadCases() {
  state.cases = await api("/cases"); const container = $("#cases-list"); container.replaceChildren();
  if (!state.cases.length) { empty(container, "No approved cases yet", "Approve a reviewed proposal to make it available to diagnoses."); return; }
  state.cases.forEach(record => {
    const card = element("article", "panel"); card.dataset.caseId = record.caseId;
    const heading = element("div", "card-title"); heading.append(element("h3", "", record.confirmedCause), element("span", "muted", `${record.application} · ${record.issueType}`));
    card.append(heading, element("p", "case-resolution", record.resolution), element("p", "outcome-summary", `${record.verifiedSuccesses} verified resolved · ${record.verifiedFailures} verified unresolved`),
      element("p", "source-line", `Approved case ${record.caseId.slice(0, 8)} · ${date(record.createdAtUtc)}`));
    const actions = element("div", "row-actions"); const inspect = element("button", "button", "Inspect signals"); inspect.type = "button";
    inspect.addEventListener("click", () => perform(actions, async () => {
      const full = await api(`/cases/${encodeURIComponent(record.caseId)}`); const existing = card.querySelector(".signals-table");
      if (existing) existing.remove(); else card.insertBefore(table(Object.entries(full.signals), ["Signal", "Stored value"]), actions);
    })); const remove = element("button", "button danger", "Delete case"); remove.type = "button";
    remove.addEventListener("click", () => { if (confirm("Delete this approved case and its recorded outcomes?")) perform(actions, async () => {
      await api(`/cases/${encodeURIComponent(record.caseId)}`, null, "DELETE"); await loadCases(); notice("Case and associated review/outcome records deleted.");
    }); }); actions.append(inspect, remove); card.append(actions);
    const form = element("form", "outcome-form"); const incident = field("Incident ID (32 hexadecimal characters)", state.requestId || ""); incident.input.minLength = 32; incident.input.maxLength = 32; incident.input.pattern = "[a-fA-F0-9]{32}";
    const outcome = element("label", "", "Resolution outcome"); const select = element("select");
    for (const [value, text] of [["true", "Resolved"], ["false", "Not resolved"]]) { const option = element("option", "", text); option.value = value; select.append(option); } outcome.append(select);
    const verified = checkbox("I verified this outcome for the incident. Reusing an ID corrects the existing record.");
    const row = element("div", "row-actions"); const submit = element("button", "button", "Record outcome"); submit.type = "submit"; row.append(submit);
    form.append(incident.wrapper, outcome, verified.wrapper, row);
    form.addEventListener("submit", event => { event.preventDefault(); perform(form, async () => {
      await api(`/cases/${encodeURIComponent(record.caseId)}/outcomes`, { incidentId: incident.input.value, resolved: select.value === "true", verified: verified.input.checked }, "POST");
      await loadCases(); notice("Verified outcome recorded. Repeated submissions for this incident are counted once.");
    }); }); card.append(form); container.append(card);
  });
}
$("#review-filter").addEventListener("change", renderReviews);
$("#refresh-documents").addEventListener("click", event => perform(event.currentTarget.parentElement, loadDocuments));
$("#refresh-reviews").addEventListener("click", event => perform(event.currentTarget.parentElement, async () => {
  if (state.role !== "reviewer") throw new Error("Connect with reviewer access first."); await loadReviews();
}));
$("#refresh-cases").addEventListener("click", event => perform(event.currentTarget.parentElement, async () => {
  if (state.role !== "reviewer") throw new Error("Connect with reviewer access first."); await loadCases();
}));
applyAccess();
