const state = {
  session: null,
  company: "",
  views: [],
  activeArea: "",
  lastRequest: null,
  lastResult: null,
  chart: null
};

const $ = (id) => document.getElementById(id);

async function api(url, opts = {}) {
  const res = await fetch(url, {
    headers: { "Content-Type": "application/json", ...(opts.headers || {}) },
    ...opts
  });
  if (!res.ok) {
    if (res.status === 401 && !url.includes("/api/auth/")) {
      showLogin();
      $("loginError").textContent = "Your session expired. Please sign in again.";
      $("loginError").classList.remove("hidden");
      throw new Error("Session expired.");
    }
    let msg = `HTTP ${res.status}`;
    try {
      const body = await res.json();
      msg = body.error || (body.errors ? body.errors.join("; ") : msg);
    } catch { }
    throw new Error(msg);
  }
  return res.status === 204 ? null : res.json();
}

async function init() {
  try {
    const users = await api("/api/auth/uat-users");
    if (users && users.length) {
      $("uatSwitcherBox").classList.remove("hidden");
      $("uatUserSelect").innerHTML = `<option value="">-- Select UAT ERP User --</option>` +
        users.map(u => `<option value="${u.userName}">${u.userName} (${u.displayName})${u.viewAllFlags ? " ★" : ""}</option>`).join("");
      $("uatUserSelect").addEventListener("change", (e) => {
        if (e.target.value) $("loginUsername").value = e.target.value;
      });
    }
  } catch { /* UAT mode disabled */ }

  try {
    const me = await api("/api/auth/me");
    await onSignedIn(me);
  } catch {
    showLogin();
  }
}

function showLogin() {
  $("loginView").classList.remove("hidden");
  $("workspaceView").classList.add("hidden");
}

async function onSignedIn(session) {
  state.session = session;
  $("loginView").classList.add("hidden");
  $("workspaceView").classList.remove("hidden");
  $("userDisplay").textContent = session.displayName || session.userName;
  $("userRolesPill").textContent = session.viewAllFlags
    ? "ViewAll"
    : (session.roles?.length ? session.roles.join(", ") : `Menu: ${(session.allowedMenuIds || []).join(",")}`);
  $("adminBtn").classList.toggle("hidden", !session.isAdmin);

  const companies = session.allowedCompanies || [];
  const opts = companies.map(c => `<option value="${c.code}">${c.code} — ${c.name}</option>`);
  if (companies.length > 1) opts.push(`<option value="ALL">ALL — All Permitted (${companies.length})</option>`);
  $("companySelect").innerHTML = opts.join("");
  state.company = companies[0]?.code || "";
  if (state.company) {
    $("companySelect").value = state.company;
    await loadAreasForCompany(state.company);
  }
}

async function loadAreasForCompany(companyCode) {
  state.company = companyCode;
  state.views = await api(`/api/schema/areas?company=${encodeURIComponent(companyCode)}`);
  const areas = [...new Set(state.views.map(v => v.areaName))];
  $("areaSelect").innerHTML = areas.map(a => `<option value="${a}">${a}</option>`).join("");
  state.activeArea = areas[0] || "";
  renderSidebarNav();
  renderViewsForArea(state.activeArea);
}

function renderSidebarNav(filter = "") {
  const q = filter.trim().toLowerCase();
  const areas = [...new Set(state.views.map(v => v.areaName))];
  const html = areas.map(area => {
    const views = state.views.filter(v => v.areaName === area &&
      (!q || area.toLowerCase().includes(q) || v.displayName.toLowerCase().includes(q) || v.viewName.toLowerCase().includes(q)));
    if (views.length === 0) return "";
    const isActive = area === state.activeArea;
    const sub = isActive ? `<div class="sidebar-sub">${views.map(v =>
      `<button type="button" class="nav-sub-item ${$("viewSelect").value === v.viewName ? "active" : ""}" data-view="${v.viewName}">${v.displayName}</button>`
    ).join("")}</div>` : "";
    return `<button type="button" class="nav-item ${isActive ? "active" : ""}" data-area="${area}"><span>${area}</span><span>${isActive ? "▾" : "▸"}</span></button>${sub}`;
  }).join("");
  $("sidebarNav").innerHTML = html;

  $("sidebarNav").querySelectorAll("[data-area]").forEach(btn => {
    btn.onclick = () => {
      state.activeArea = btn.dataset.area;
      $("areaSelect").value = state.activeArea;
      renderViewsForArea(state.activeArea);
      renderSidebarNav($("menuSearch").value);
    };
  });
  $("sidebarNav").querySelectorAll("[data-view]").forEach(btn => {
    btn.onclick = () => {
      $("viewSelect").value = btn.dataset.view;
      selectView(btn.dataset.view);
      renderSidebarNav($("menuSearch").value);
    };
  });
}

function renderViewsForArea(area) {
  state.activeArea = area;
  const filtered = state.views.filter(v => v.areaName === area);
  $("viewSelect").innerHTML = filtered.map(v => `<option value="${v.viewName}">${v.displayName} (rpt.${v.viewName})</option>`).join("");
  if (filtered[0]) selectView(filtered[0].viewName);
}

function selectView(viewName, preferredFields = null) {
  const view = state.views.find(v => v.viewName === viewName);
  if (!view) return;
  $("breadcrumb").textContent = `Home  |  ${state.company}  |  ${view.areaName}  |  ${view.displayName}`;
  $("pageHeading").textContent = view.displayName;
  $("viewDesc").textContent = view.description || "";
  const defaults = new Set(preferredFields || view.defaultFields || []);
  $("fieldsChecklist").innerHTML = view.columns.map(c => `
    <div class="check-item">
      <label><input type="checkbox" value="${c.name}" ${defaults.has(c.name) ? "checked" : ""}/> ${c.name}</label>
      <span class="type-tag">${c.dataType}</span>
    </div>`).join("");

  $("sortColSelect").innerHTML = `<option value="">None</option>` +
    view.columns.map(c => `<option value="${c.name}">${c.name}</option>`).join("");
  $("aggList").innerHTML = "";
  $("filterList").innerHTML = "";
}

function currentView() {
  return state.views.find(v => v.viewName === $("viewSelect").value);
}

function addAggregationRow(agg = { column: "", function: "SUM", alias: "" }) {
  const view = currentView();
  if (!view) return;
  const row = document.createElement("div");
  row.className = "inline-row";
  row.innerHTML = `
    <select class="agg-fn">
      ${["SUM", "COUNT", "AVG", "MIN", "MAX"].map(f => `<option ${agg.function === f ? "selected" : ""}>${f}</option>`).join("")}
    </select>
    <select class="agg-col">
      <option value="">*</option>
      ${view.columns.map(c => `<option value="${c.name}" ${agg.column === c.name ? "selected" : ""}>${c.name}</option>`).join("")}
    </select>
    <input class="agg-alias" type="text" placeholder="alias" value="${agg.alias || ""}" />
    <button type="button" class="btn-link rm">×</button>`;
  row.querySelector(".rm").onclick = () => row.remove();
  $("aggList").appendChild(row);
}

function addFilterRow(f = { column: "", op: "=", value: "" }) {
  const view = currentView();
  if (!view) return;
  const row = document.createElement("div");
  row.className = "inline-row";
  row.innerHTML = `
    <select class="flt-col">
      ${view.columns.map(c => `<option value="${c.name}" ${f.column === c.name ? "selected" : ""}>${c.name}</option>`).join("")}
    </select>
    <select class="flt-op">
      ${["=", "<>", ">", ">=", "<", "<=", "ILIKE"].map(o => `<option ${f.op === o ? "selected" : ""}>${o}</option>`).join("")}
    </select>
    <input class="flt-val" type="text" placeholder="value" value="${f.value || ""}" />
    <button type="button" class="btn-link rm">×</button>`;
  row.querySelector(".rm").onclick = () => row.remove();
  $("filterList").appendChild(row);
}

function collectRequest() {
  const selectedColumns = [...$("fieldsChecklist").querySelectorAll("input:checked")].map(i => i.value);
  const aggregations = [...$("aggList").querySelectorAll(".inline-row")].map(r => ({
    function: r.querySelector(".agg-fn").value,
    column: r.querySelector(".agg-col").value,
    alias: r.querySelector(".agg-alias").value.trim()
  }));
  const filters = [...$("filterList").querySelectorAll(".inline-row")].map(r => ({
    column: r.querySelector(".flt-col").value,
    op: r.querySelector(".flt-op").value,
    value: r.querySelector(".flt-val").value
  })).filter(f => f.value !== "");
  const sortCol = $("sortColSelect").value;
  const orderBy = sortCol ? [{ column: sortCol, direction: $("sortDirSelect").value }] : [];
  return {
    viewName: $("viewSelect").value,
    companyCode: state.company,
    selectedColumns,
    aggregations,
    filters,
    relativePeriod: $("periodSelect").value,
    limit: parseInt($("limitInput").value || "200", 10),
    orderBy,
    chartType: $("chartTypeSelect").value
  };
}

function applyAiRequest(req) {
  const view = state.views.find(v => v.viewName === req.viewName);
  if (!view) return;
  $("areaSelect").value = view.areaName;
  renderViewsForArea(view.areaName);
  $("viewSelect").value = view.viewName;
  selectView(view.viewName, req.selectedColumns);
  renderSidebarNav($("menuSearch").value);
  (req.aggregations || []).forEach(addAggregationRow);
  (req.filters || []).forEach(addFilterRow);
  if (req.relativePeriod) $("periodSelect").value = req.relativePeriod;
  if (req.limit) $("limitInput").value = req.limit;
  if (req.orderBy?.[0]) {
    if ([...$("sortColSelect").options].every(o => o.value !== req.orderBy[0].column)) {
      $("sortColSelect").innerHTML += `<option value="${req.orderBy[0].column}">${req.orderBy[0].column}</option>`;
    }
    $("sortColSelect").value = req.orderBy[0].column;
    $("sortDirSelect").value = req.orderBy[0].direction || "DESC";
  }
}

async function runReport(req) {
  const btn = $("runReportBtn");
  const prevText = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Generating...";
  state.lastRequest = req;
  $("reconcileBox").classList.add("hidden");
  try {
    const data = await api("/api/reports/run", { method: "POST", body: JSON.stringify(req) });
    state.lastResult = data.result;
    $("resultTitle").textContent = `${data.displayName} (${data.companyCode})`;
    $("resultMeta").textContent = `Rows: ${data.result.rows.length}${data.result.truncated ? " (truncated)" : ""} | Audit Run #${data.runId}`;
    $("sqlPreview").textContent = data.sql;
    $("viewToggle").classList.remove("hidden");
    document.querySelectorAll(".export-btn").forEach(b => b.disabled = false);
    renderTable(data.result);
    renderChart(data.result);
  } finally {
    btn.disabled = false;
    btn.textContent = prevText;
  }
}

async function runReconcile(question, areas) {
  const data = await api("/api/reconcile", {
    method: "POST",
    body: JSON.stringify({ companyCode: state.company, question, areas })
  });
  $("reconcileBox").classList.remove("hidden");
  $("reconcileBox").innerHTML = `<strong>Reconciliation Summary (Run #${data.runId})</strong><p>${data.summary}</p>`;
  const result = {
    columns: ["area", "gl_balance", "subledger_balance", "difference", "explanation"],
    rows: data.areas.map(a => [a.area, a.glBalance, a.subledgerBalance, a.difference, a.explanation]),
    truncated: false
  };
  state.lastResult = result;
  $("resultTitle").textContent = `Ledger vs Trial Balance Reconciliation (${data.companyCode})`;
  $("resultMeta").textContent = `Trial Balance Net: ${data.trialBalanceNet} | Audit Run #${data.runId}`;
  $("viewToggle").classList.remove("hidden");
  renderTable(result);
}

function populateGroupSelect(result) {
  const sel = $("uiGroupSelect");
  if (!sel) return;
  const prev = sel.value;
  sel.innerHTML = `<option value="">No Grouping</option>` +
    result.columns.map((c, i) => `<option value="${i}">${c}</option>`).join("");
  if (prev !== "" && Number(prev) < result.columns.length) sel.value = prev;
  else {
    const bucketIdx = result.columns.findIndex(c => c.toLowerCase() === "aging_bucket");
    sel.value = bucketIdx >= 0 ? String(bucketIdx) : "";
  }
}

function fmtVal(v) {
  if (v === null || v === undefined) return "";
  if (typeof v === "number") return Number.isInteger(v) ? v.toLocaleString() : v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  return String(v);
}

function renderTable(result, filter = "") {
  const thead = $("resultTable").querySelector("thead");
  const tbody = $("resultTable").querySelector("tbody");
  thead.innerHTML = `<tr>${result.columns.map(c => `<th>${c}</th>`).join("")}</tr>`;
  const term = filter.trim().toLowerCase();
  const rows = term ? result.rows.filter(r => r.some(v => String(v ?? "").toLowerCase().includes(term))) : result.rows;

  const grpVal = $("uiGroupSelect")?.value ?? "";
  if (grpVal === "" || rows.length === 0) {
    tbody.innerHTML = rows.map(r => `<tr>${r.map(v => `<td>${fmtVal(v)}</td>`).join("")}</tr>`).join("");
    return;
  }

  const grpIdx = Number(grpVal);
  const groups = new Map();
  for (const r of rows) {
    const key = String(r[grpIdx] ?? "(Blank)");
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(r);
  }

  const numCols = result.columns.map((_, i) => i !== grpIdx && rows.some(r => typeof r[i] === "number"));
  let html = "";
  for (const [key, grpRows] of groups.entries()) {
    html += `<tr class="group-header"><td colspan="${result.columns.length}">${result.columns[grpIdx]}: ${key} (${grpRows.length} rows)</td></tr>`;
    html += grpRows.map(r => `<tr>${r.map(v => `<td>${fmtVal(v)}</td>`).join("")}</tr>`).join("");
    if (numCols.some(Boolean)) {
      const sums = result.columns.map((_, i) => {
        if (i === grpIdx) return `Subtotal (${key})`;
        if (!numCols[i]) return "";
        const total = grpRows.reduce((acc, r) => acc + (typeof r[i] === "number" ? r[i] : 0), 0);
        return fmtVal(total);
      });
      html += `<tr class="group-subtotal">${sums.map(s => `<td>${s}</td>`).join("")}</tr>`;
    }
  }
  tbody.innerHTML = html;
}

const CHART_PALETTE = [
  "#f15a24", "#2563eb", "#10b981", "#f59e0b", "#8b5cf6", "#06b6d4",
  "#ec4899", "#14b8a6", "#f97316", "#6366f1", "#84cc16", "#ef4444"
];

function isNumValue(v) {
  return typeof v === "number" || (typeof v === "string" && v.trim() !== "" && !isNaN(Number(v)));
}

function populateChartMetricSelect(result) {
  const sel = $("chartMetricSelect");
  if (!sel || !result || result.rows.length === 0) return;
  const numCols = result.columns
    .map((c, i) => ({ name: c, i }))
    .filter(x => result.rows.some(r => isNumValue(r[x.i])));

  let opts = "";
  if (numCols.length >= 2) {
    opts += `<option value="__summary__">Compare All Numeric Columns (Totals)</option>`;
  }
  opts += numCols.map(c => `<option value="${c.i}">${c.name}</option>`).join("");
  sel.innerHTML = opts;

  if (numCols.length >= 2 && numCols.some(c => c.name.startsWith("ar_"))) {
    sel.value = "__summary__";
  } else if (numCols.length > 0) {
    const preferred = numCols.find(c => c.name.includes("total") || c.name.includes("balance") || c.name.includes("amount") || c.name.includes("qty")) || numCols[0];
    sel.value = String(preferred.i);
  }
}

function renderChart(result) {
  if (state.chart) state.chart.destroy();
  if ($("chartNote")) $("chartNote").textContent = "";
  if (!result || result.columns.length < 2 || result.rows.length === 0) return;

  const type = $("chartTypeSelect").value;
  const metricVal = $("chartMetricSelect")?.value ?? "";
  const useLog = $("chartLogScale")?.checked && type === "bar";

  let labels = [];
  let values = [];
  let seriesLabel = "";

  if (metricVal === "__summary__") {
    const numCols = result.columns
      .map((c, i) => ({ name: c, i }))
      .filter(x => !x.name.toLowerCase().includes("total_") && result.rows.some(r => isNumValue(r[x.i])));
    const colsToSum = numCols.length > 0 ? numCols : result.columns.map((c, i) => ({ name: c, i })).filter(x => result.rows.some(r => isNumValue(r[x.i])));
    labels = colsToSum.map(c => c.name);
    values = colsToSum.map(c => result.rows.reduce((acc, r) => acc + (isNumValue(r[c.i]) ? Number(r[c.i]) : 0), 0));
    seriesLabel = "Total by Metric";
  } else {
    const isNumCol = (i) => result.rows.some(r => isNumValue(r[i]));
    const valIdx = metricVal !== "" ? Number(metricVal) : Math.max(0, result.columns.findIndex((_, i) => isNumCol(i)));
    seriesLabel = result.columns[valIdx] || "Value";

    const textCols = result.columns
      .map((c, i) => ({ name: c, i }))
      .filter(x => x.i !== valIdx && !isNumCol(x.i));
    const preferred =
      textCols.find(x => x.name.toLowerCase() !== "company_code" && x.name.toLowerCase().includes("name")) ||
      textCols.find(x => x.name.toLowerCase() !== "company_code") ||
      textCols[0] ||
      { i: 0 };
    const labelIdx = preferred.i;

    let pairs = result.rows.map(r => ({
      label: String(r[labelIdx] ?? "(Blank)").trim() || "(Blank)",
      val: Number(r[valIdx] ?? 0)
    }));

    pairs = pairs.filter(p => p.val > 0).sort((a, b) => b.val - a.val);
    const maxItems = type === "pie" ? 8 : 15;
    if (pairs.length > maxItems) {
      const top = pairs.slice(0, maxItems);
      if (type === "pie") {
        const rest = pairs.slice(maxItems).reduce((s, p) => s + p.val, 0);
        if (rest > 0) top.push({ label: `Other (${pairs.length - maxItems})`, val: rest });
      }
      pairs = top;
    }

    labels = pairs.map(p => p.label);
    values = pairs.map(p => p.val);
  }

  const total = values.reduce((a, b) => a + b, 0);
  if (total > 0 && values.some(v => v / total > 0.95) && $("chartNote")) {
    $("chartNote").textContent = "Note: One outlier value accounts for >95% of the total. Enable 'Log scale' (Bar) or use Line Details to inspect smaller values.";
  }

  const displayLabels = type === "pie" && total > 0
    ? labels.map((l, i) => `${l} (${((values[i] / total) * 100).toFixed(1)}%)`)
    : labels;

  const colors = labels.map((_, i) => CHART_PALETTE[i % CHART_PALETTE.length]);

  const dataset = type === "line"
    ? {
      label: seriesLabel,
      data: values,
      borderColor: "#f15a24",
      backgroundColor: "rgba(241, 90, 36, 0.14)",
      pointBackgroundColor: colors,
      pointBorderColor: "#ffffff",
      pointRadius: 5,
      borderWidth: 2.5,
      tension: 0.25,
      fill: true
    }
    : {
      label: seriesLabel,
      data: values,
      backgroundColor: colors,
      borderColor: type === "pie" ? "#ffffff" : colors,
      borderWidth: type === "pie" ? 2 : 1,
      borderRadius: type === "bar" ? 4 : 0
    };

  state.chart = new Chart($("reportChart"), {
    type,
    data: { labels: displayLabels, datasets: [dataset] },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      scales: type === "bar" ? { y: { type: useLog ? "logarithmic" : "linear" } } : undefined,
      plugins: {
        legend: { display: type === "pie", position: "right" }
      }
    }
  });
}

async function exportReport(fmt) {
  if (!state.lastRequest) return;
  const res = await fetch(`/api/reports/export/${fmt}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(state.lastRequest)
  });
  if (!res.ok) return alert("Export failed.");
  const blob = await res.blob();
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = `${state.lastRequest.viewName}_${state.company}.${fmt}`;
  a.click();
}

// Event bindings
$("togglePwdBtn").onclick = () => {
  const inp = $("loginPassword");
  const show = inp.type === "password";
  inp.type = show ? "text" : "password";
  $("togglePwdBtn").textContent = show ? "Hide" : "Show";
};

$("loginBtn").onclick = async () => {
  try {
    $("loginError").classList.add("hidden");
    const s = await api("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ userName: $("loginUsername").value, password: $("loginPassword").value })
    });
    await onSignedIn(s);
  } catch (e) {
    $("loginError").textContent = e.message;
    $("loginError").classList.remove("hidden");
  }
};
$("logoutBtn").onclick = async () => { await api("/api/auth/logout", { method: "POST" }); showLogin(); };
$("switchUserBtn").onclick = () => showLogin();
$("menuSearch").oninput = (e) => renderSidebarNav(e.target.value);
$("companySelect").onchange = (e) => loadAreasForCompany(e.target.value);
$("areaSelect").onchange = (e) => { renderViewsForArea(e.target.value); renderSidebarNav($("menuSearch").value); };
$("viewSelect").onchange = (e) => { selectView(e.target.value); renderSidebarNav($("menuSearch").value); };
$("selectDefaultFieldsBtn").onclick = () => selectView($("viewSelect").value);
$("addAggBtn").onclick = () => addAggregationRow();
$("addFilterBtn").onclick = () => addFilterRow();
$("runReportBtn").onclick = () => {
  $("aiNotice").classList.add("hidden");
  runReport(collectRequest()).catch(e => {
    $("aiNotice").textContent = e.message;
    $("aiNotice").className = "alert alert-error";
  });
};
$("tableSearch").oninput = (e) => state.lastResult && renderTable(state.lastResult, e.target.value);
if ($("uiGroupSelect")) $("uiGroupSelect").onchange = () => state.lastResult && renderTable(state.lastResult, $("tableSearch").value);
$("chartTypeSelect").onchange = () => state.lastResult && renderChart(state.lastResult);
if ($("chartMetricSelect")) $("chartMetricSelect").onchange = () => state.lastResult && renderChart(state.lastResult);
if ($("chartLogScale")) $("chartLogScale").onchange = () => state.lastResult && renderChart(state.lastResult);

document.querySelectorAll(".chip").forEach(c => c.onclick = () => { $("aiPrompt").value = c.dataset.prompt; });
document.querySelectorAll(".export-btn").forEach(b => b.onclick = () => exportReport(b.dataset.fmt));
document.querySelectorAll(".tab").forEach(t => t.onclick = () => {
  document.querySelectorAll(".tab").forEach(x => x.classList.remove("active"));
  t.classList.add("active");
  $("tableTab").classList.toggle("hidden", t.dataset.tab !== "table");
  $("chartTab").classList.toggle("hidden", t.dataset.tab !== "chart");
  $("sqlTab").classList.toggle("hidden", t.dataset.tab !== "sql");
  if (t.dataset.tab === "chart" && state.lastResult) renderChart(state.lastResult);
});

async function handleAi(autoRun) {
  const q = $("aiPrompt").value.trim();
  if (!q) return;
  const cfgBtn = $("aiConfigBtn");
  const runBtn = $("aiRunBtn");
  cfgBtn.disabled = true;
  runBtn.disabled = true;
  const activeBtn = autoRun ? runBtn : cfgBtn;
  const prevText = activeBtn.textContent;
  activeBtn.textContent = "Thinking...";
  $("aiNotice").classList.add("hidden");
  try {
    const res = await api("/api/reports/ai-configure", {
      method: "POST",
      body: JSON.stringify({ question: q, companyCode: state.company })
    });
    if (res.missingDataNote) {
      $("aiNotice").textContent = res.missingDataNote;
      $("aiNotice").className = "alert alert-error";
      return;
    }
    if (res.isReconcile) {
      $("aiNotice").textContent = res.explanation || "Routing to deterministic GL / Trial Balance reconciler...";
      $("aiNotice").className = "alert";
      if (autoRun) await runReconcile(q, res.reconcileAreas);
      return;
    }
    if (res.request) {
      applyAiRequest(res.request);
      $("aiNotice").textContent = res.explanation || "Configuration populated below. Review fields or click Generate Report.";
      $("aiNotice").className = "alert";
      if (autoRun) await runReport(collectRequest());
    }
  } catch (e) {
    $("aiNotice").textContent = e.message;
    $("aiNotice").className = "alert alert-error";
  } finally {
    cfgBtn.disabled = false;
    runBtn.disabled = false;
    activeBtn.textContent = prevText;
  }
}

$("aiConfigBtn").onclick = () => handleAi(false);
$("aiRunBtn").onclick = () => handleAi(true);

// Admin RBAC modal
$("adminBtn").onclick = async () => {
  $("adminModal").classList.remove("hidden");
  await refreshRbacTable();
};
$("closeAdminBtn").onclick = () => $("adminModal").classList.add("hidden");
$("saveRuleBtn").onclick = async () => {
  await api("/api/admin/rbac-map", {
    method: "POST",
    body: JSON.stringify({
      roleName: $("ruleRole").value || null,
      menuId: $("ruleMenuId").value ? parseInt($("ruleMenuId").value, 10) : null,
      areaName: $("ruleArea").value || null,
      viewName: $("ruleView").value,
      blockedColumns: $("ruleBlocked").value || ""
    })
  });
  await refreshRbacTable();
};

async function refreshRbacTable() {
  const rules = await api("/api/admin/rbac-map");
  $("rbacTable").querySelector("tbody").innerHTML = rules.map(r => `
    <tr>
      <td>${r.id}</td><td>${r.roleName ?? ""}</td><td>${r.menuId ?? ""}</td>
      <td>${r.areaName ?? ""}</td><td>${r.viewName}</td><td>${r.blockedColumns}</td>
      <td><button class="btn-link" onclick="deleteRule(${r.id})">Delete</button></td>
    </tr>`).join("");
}
window.deleteRule = async (id) => { await api(`/api/admin/rbac-map/${id}`, { method: "DELETE" }); await refreshRbacTable(); };

init();
