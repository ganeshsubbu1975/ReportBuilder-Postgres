const state = {
  session: null,
  company: "",
  views: [],
  activeArea: "",
  lastRequest: null,
  lastHavingFilters: [],
  savedReports: [],
  lastResult: null,
  chart: null,
  chartDirty: false
};

function escHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]));
}

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
  await loadSavedReports();
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
    havingFilters: state.lastHavingFilters || [],
    relativePeriod: $("periodSelect").value,
    limit: parseInt($("limitInput").value || "200", 10),
    orderBy,
    chartType: $("chartTypeSelect").value
  };
}

function applyAiRequest(req) {
  const view = state.views.find(v => v.viewName === req.viewName);
  if (!view) return;
  state.lastHavingFilters = req.havingFilters || [];
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
  if (req.chartType && $("chartTypeSelect")) {
    const ct = String(req.chartType).trim().toLowerCase();
    if (SUPPORTED_CHART_TYPES.has(ct)) $("chartTypeSelect").value = ct;
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
    populateGroupSelect(data.result);
    renderTable(data.result);
    syncAndRenderChart(data.result, data.chartType);
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
  populateGroupSelect(result);
  renderTable(result);
  syncAndRenderChart(result, "bar");
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
  "#2563eb", "#f15a24", "#10b981", "#8b5cf6", "#f59e0b", "#06b6d4",
  "#ec4899", "#14b8a6", "#6366f1", "#84cc16", "#ef4444", "#64748b"
];
const OTHER_SLICE_COLOR = "#94a3b8";
const SUPPORTED_CHART_TYPES = new Set(["bar", "hbar", "line", "pie", "doughnut"]);
const ID_CODE_COL_RE = /^(id|.*_id|.*_no|.*_num|.*_code|.*code|fiscal_year|year|month_no|quarter_no|period_no|menuid|user_type|company_id)$/i;
const DATE_COL_RE = /(date|_dt|month|quarter|period|week|day)/i;
const META_DIM_RE = /^(company_code|company_name|company|branch_code|branch_name|branch|currency_code|currency_name|currency|uom|unit|approve_status|status|is_reconciled|is_control_account)$/i;
const ENTITY_DIM_RE = /(vendor_name|supplier_name|customer_name|partner_name|business_partner_name|party_name|account_name|ledger_account_name|item_name|product_name|employee_name|sales_rep|bank_account_name|bank_name|warehouse_name|department_name|aging_bucket|area|sales_region|sales_category|return_reason|source_type)/i;

function formatColLabel(name) {
  const s = String(name ?? "").trim();
  if (!s) return "";
  const acronyms = { ar: "AR", gl: "GL", tb: "TB", id: "ID", ytd: "YTD", ap: "AP", po: "PO", so: "SO" };
  return s
    .split("_")
    .filter(Boolean)
    .map(p => {
      const lo = p.toLowerCase();
      if (acronyms[lo]) return acronyms[lo];
      if (/^\d+$/.test(p)) return p;
      return lo.charAt(0).toUpperCase() + lo.slice(1);
    })
    .join(" ")
    .replace(/(\d+) (\d+)/g, "$1-$2");
}

function formatCompactNumber(v) {
  const n = Number(v);
  if (!Number.isFinite(n)) return String(v ?? "");
  const abs = Math.abs(n);
  if (abs >= 1e9) return `${(n / 1e9).toFixed(2).replace(/\.00$/, "")}B`;
  if (abs >= 1e6) return `${(n / 1e6).toFixed(2).replace(/\.00$/, "")}M`;
  if (abs >= 1e3) return `${(n / 1e3).toFixed(1).replace(/\.0$/, "")}K`;
  return Number.isInteger(n) ? n.toLocaleString() : n.toLocaleString(undefined, { maximumFractionDigits: 2 });
}

function truncateLabel(text, maxLen = 26) {
  const s = String(text ?? "");
  return s.length > maxLen ? `${s.slice(0, maxLen - 1)}…` : s;
}

function formatCellLabel(val, isDateCol = false) {
  if (val === null || val === undefined) return "(Blank)";
  const s = String(val).trim();
  if (!s) return "(Blank)";
  if (isDateCol || /^\d{4}-\d{2}-\d{2}T/.test(s)) {
    const m = s.match(/^(\d{4}-\d{2}-\d{2})/);
    if (m) return m[1];
  }
  return s;
}

function isStrictNumericValue(v) {
  if (typeof v === "number") return Number.isFinite(v);
  if (typeof v !== "string") return false;
  const s = v.trim();
  if (!s || /^0\d+/.test(s)) return false;
  return /^-?\d+(?:\.\d+)?$/.test(s);
}

function parseChartNum(v) {
  if (typeof v === "number") return Number.isFinite(v) ? v : 0;
  const n = Number(String(v ?? "").trim());
  return Number.isFinite(n) ? n : 0;
}

function showChartNote(msg) {
  const el = $("chartNote");
  if (!el) return;
  if (!msg) {
    el.textContent = "";
    el.classList.add("hidden");
  } else {
    el.textContent = msg;
    el.classList.remove("hidden");
  }
}

function classifyColumns(result) {
  const rows = result?.rows || [];
  const cols = (result?.columns || []).map((name, i) => {
    const nonNull = rows.map(r => r[i]).filter(v => v !== null && v !== undefined && String(v).trim() !== "");
    const allNum = nonNull.length > 0 && nonNull.every(isStrictNumericValue);
    const hasNativeNum = nonNull.some(v => typeof v === "number");
    const isIdLike = ID_CODE_COL_RE.test(name);
    const isDateVal = nonNull.length > 0 && nonNull.every(v => /^\d{4}-\d{2}(?:-\d{2})?/.test(String(v).trim()));
    const isDate = !hasNativeNum && (isDateVal || (DATE_COL_RE.test(name) && !/_days$/i.test(name)));
    const distinctCount = new Set(rows.map(r => formatCellLabel(r[i], isDate))).size;
    return {
      i,
      name,
      label: formatColLabel(name),
      isMetric: allNum && !isIdLike,
      isAnyNum: allNum,
      isDate,
      distinctCount
    };
  });

  let metricCols = cols.filter(c => c.isMetric);
  if (metricCols.length === 0) metricCols = cols.filter(c => c.isAnyNum);
  const metricIdxSet = new Set(metricCols.map(c => c.i));
  let dimensionCols = cols.filter(c => !metricIdxSet.has(c.i));
  if (dimensionCols.length === 0) dimensionCols = cols;
  return { cols, metricCols, dimensionCols };
}

function pickDefaultDimension(dimensionCols, chartType) {
  if (!dimensionCols.length) return null;
  const varying = dimensionCols.filter(c => c.distinctCount >= 2);
  const pool = varying.length > 0 ? varying : dimensionCols;
  if (chartType === "line") {
    const dtCol = pool.find(c => c.isDate);
    if (dtCol) return dtCol;
  }
  return (
    pool.find(c => !META_DIM_RE.test(c.name) && ENTITY_DIM_RE.test(c.name)) ||
    pool.find(c => !META_DIM_RE.test(c.name) && /(name|title|description|label|bucket|category|region|group|type)/i.test(c.name)) ||
    pool.find(c => !META_DIM_RE.test(c.name)) ||
    pool[0]
  );
}

function populateChartControls(result) {
  const labelSel = $("chartLabelSelect");
  const metricSel = $("chartMetricSelect");
  if (!metricSel || !result) return;

  const colSig = (result.columns || []).join("|");
  const sameShape = colSig === state.lastChartColSig;
  state.lastChartColSig = colSig;

  const { metricCols, dimensionCols } = classifyColumns(result);
  const type = $("chartTypeSelect")?.value || "bar";

  if (labelSel) {
    const prevColName = sameShape ? (labelSel.options[labelSel.selectedIndex]?.dataset?.colName || "") : "";
    labelSel.innerHTML = dimensionCols
      .map(c => `<option value="${c.i}" data-col-name="${escHtml(c.name)}">${escHtml(c.label)} (${c.distinctCount})</option>`)
      .join("");
    const kept = prevColName ? dimensionCols.find(c => c.name === prevColName) : null;
    const keepAllowed = kept && (kept.distinctCount >= 2 || dimensionCols.every(c => c.distinctCount <= 1));
    const defDim = (keepAllowed ? kept : null) || pickDefaultDimension(dimensionCols, type);
    if (defDim) labelSel.value = String(defDim.i);
  }

  const prevMetricKey = sameShape ? (metricSel.options[metricSel.selectedIndex]?.dataset?.metricKey || "") : "";
  let opts = "";
  if (metricCols.length >= 2) {
    opts += `<option value="__grouped__" data-metric-key="__grouped__">Compare All Metrics (Grouped)</option>`;
    opts += `<option value="__summary__" data-metric-key="__summary__">Totals Across Metrics (Summary)</option>`;
  }
  opts += metricCols
    .map(c => `<option value="${c.i}" data-metric-key="${escHtml(c.name)}">${escHtml(c.label)}</option>`)
    .join("");
  metricSel.innerHTML = opts;

  const isMultiMetricComparison = metricCols.length >= 2 && (
    metricCols.some(c => /^(ar_|ap_|aging_|bucket_)/i.test(c.name)) ||
    (metricCols.some(c => c.name.toLowerCase() === "gl_balance") && metricCols.some(c => c.name.toLowerCase() === "subledger_balance"))
  );

  if (prevMetricKey === "__grouped__" && metricCols.length >= 2) {
    metricSel.value = "__grouped__";
  } else if (prevMetricKey === "__summary__" && metricCols.length >= 2) {
    metricSel.value = "__summary__";
  } else if (prevMetricKey && metricCols.some(c => c.name === prevMetricKey)) {
    metricSel.value = String(metricCols.find(c => c.name === prevMetricKey).i);
  } else if (isMultiMetricComparison) {
    metricSel.value = (type === "pie" || type === "doughnut") ? "__summary__" : "__grouped__";
  } else if (metricCols.length > 0) {
    const preferred =
      metricCols.find(c => /total|net|gross|amount|balance|sales|qty|quantity|value|diff/i.test(c.name)) ||
      metricCols[0];
    metricSel.value = String(preferred.i);
  }
}

function syncAndRenderChart(result, preferredChartType = null) {
  const typeSel = $("chartTypeSelect");
  if (typeSel && preferredChartType) {
    const norm = String(preferredChartType).trim().toLowerCase();
    if (SUPPORTED_CHART_TYPES.has(norm)) typeSel.value = norm;
  }
  populateChartControls(result);
  const chartTabHidden = $("chartTab")?.classList.contains("hidden");
  if (chartTabHidden) {
    state.chartDirty = true;
    if (state.chart) {
      state.chart.destroy();
      state.chart = null;
    }
  } else {
    state.chartDirty = false;
    renderChart(result);
  }
}

function aggregateByCategory(rows, labelCol, metricColsList) {
  const map = new Map();
  for (const r of rows) {
    const key = formatCellLabel(r[labelCol.i], labelCol.isDate);
    let entry = map.get(key);
    if (!entry) {
      const sortTs = labelCol.isDate ? Date.parse(key) : NaN;
      entry = { label: key, vals: new Array(metricColsList.length).fill(0), sortTs };
      map.set(key, entry);
    }
    for (let m = 0; m < metricColsList.length; m++) {
      entry.vals[m] += parseChartNum(r[metricColsList[m].i]);
    }
  }
  const list = [...map.values()];
  if (labelCol.isDate && list.every(x => Number.isFinite(x.sortTs))) {
    list.sort((a, b) => a.sortTs - b.sortTs);
  } else if (labelCol.isDate) {
    list.sort((a, b) => a.label.localeCompare(b.label));
  } else {
    list.sort((a, b) => (b.vals[0] - a.vals[0]) || a.label.localeCompare(b.label));
  }
  return list;
}

function buildRadialLegendPlugin(fullLabels, values, colors, totalSum) {
  const maxLen = window.innerWidth < 820 ? 22 : 34;
  return {
    display: true,
    position: window.innerWidth < 820 ? "bottom" : "right",
    labels: {
      boxWidth: 14,
      padding: 12,
      usePointStyle: true,
      pointStyle: "circle",
      generateLabels(chart) {
        return fullLabels.map((lbl, i) => {
          const pct = totalSum > 0 ? ((values[i] / totalSum) * 100).toFixed(1) : "0.0";
          const visible = chart.getDataVisibility(i);
          return {
            text: `${truncateLabel(lbl, maxLen)} — ${formatCompactNumber(values[i])} (${pct}%)`,
            fillStyle: colors[i],
            strokeStyle: colors[i],
            fontColor: visible ? "#334155" : "#94a3b8",
            hidden: !visible,
            index: i
          };
        });
      }
    },
    onClick(_e, legendItem, legend) {
      legend.chart.toggleDataVisibility(legendItem.index);
      legend.chart.update();
    }
  };
}

function renderChart(result) {
  if (state.chart) {
    state.chart.destroy();
    state.chart = null;
  }
  showChartNote("");
  if (!result || !result.rows || result.rows.length === 0) {
    showChartNote("No rows returned to plot.");
    return;
  }

  const { cols, metricCols, dimensionCols } = classifyColumns(result);
  if (metricCols.length === 0) {
    showChartNote("This result has no numeric metric columns to chart. View Line Details instead.");
    return;
  }

  const type = $("chartTypeSelect")?.value || "bar";
  const isRadial = type === "pie" || type === "doughnut";
  const isHorizontal = type === "hbar";
  const chartJsType = isHorizontal ? "bar" : type;

  if ($("chartLogWrap")) $("chartLogWrap").classList.toggle("hidden", isRadial);

  const labelIdxStr = $("chartLabelSelect")?.value ?? "";
  const labelCol = cols.find(c => String(c.i) === labelIdxStr) || pickDefaultDimension(dimensionCols, type) || cols[0];
  let metricVal = $("chartMetricSelect")?.value ?? "";
  if (!metricVal) metricVal = String(metricCols[0].i);

  const limitRaw = parseInt($("chartLimitSelect")?.value ?? "15", 10);
  const limit = Number.isFinite(limitRaw) && limitRaw > 0 ? limitRaw : 0;
  const wantLog = Boolean($("chartLogScale")?.checked) && !isRadial;

  const hasBucketRollup = metricCols.some(c => /^(ar_\d|ap_\d|aging_|bucket_)/i.test(c.name)) && metricCols.some(c => /^total_/i.test(c.name));
  const summaryCols = hasBucketRollup ? metricCols.filter(c => !/^total_/i.test(c.name)) : metricCols;

  const notes = [];
  let fullLabels = [];
  let datasets = [];
  let chartTitle = "";
  let categoryAxisTitle = "";
  let valueAxisTitle = "";
  let customLegend = { display: true, position: "top" };
  let isSingleSeries = true;
  let positiveSumForPct = 0;

  if (metricVal === "__summary__" || (isRadial && metricVal === "__grouped__")) {
    if (isRadial && metricVal === "__grouped__") {
      notes.push("Pie/Doughnut shows total share across metrics. Pick a single Metric to slice by category, or use Bar/Line for grouped comparison.");
    }
    let items = summaryCols.map(c => ({
      label: c.label,
      val: result.rows.reduce((acc, r) => acc + parseChartNum(r[c.i]), 0)
    }));

    chartTitle = "Totals Across Metrics";
    categoryAxisTitle = "Metric";
    valueAxisTitle = "Total Value";

    if (isRadial) {
      const totalCount = items.length;
      items = items.filter(x => x.val > 0);
      const omitted = totalCount - items.length;
      if (omitted > 0) {
        notes.push(`${omitted} zero/negative metric${omitted === 1 ? "" : "s"} omitted from ${type === "pie" ? "Pie" : "Doughnut"}. Use Bar chart to view negative values.`);
      }
      if (items.length === 0) {
        showChartNote("No positive metric totals available for Pie/Doughnut. Switch to Bar chart.");
        return;
      }
      fullLabels = items.map(x => x.label);
      const vals = items.map(x => x.val);
      const colors = items.map((_, i) => CHART_PALETTE[i % CHART_PALETTE.length]);
      positiveSumForPct = vals.reduce((a, b) => a + b, 0);
      datasets = [{
        label: "Total",
        data: vals,
        backgroundColor: colors,
        borderColor: "#ffffff",
        borderWidth: 2
      }];
      customLegend = buildRadialLegendPlugin(fullLabels, vals, colors, positiveSumForPct);
    } else if (type === "line") {
      fullLabels = items.map(x => x.label);
      const vals = items.map(x => x.val);
      positiveSumForPct = vals.filter(v => v > 0).reduce((a, b) => a + b, 0);
      datasets = [{
        label: "Metric Totals",
        data: vals,
        borderColor: "#f15a24",
        backgroundColor: "rgba(241, 90, 36, 0.12)",
        pointBackgroundColor: "#f15a24",
        pointBorderColor: "#ffffff",
        pointRadius: 5,
        borderWidth: 2.5,
        tension: 0.25,
        fill: true
      }];
    } else {
      fullLabels = items.map(x => x.label);
      const vals = items.map(x => x.val);
      const colors = items.map((_, i) => CHART_PALETTE[i % CHART_PALETTE.length]);
      positiveSumForPct = vals.filter(v => v > 0).reduce((a, b) => a + b, 0);
      datasets = [{
        label: "Metric Total",
        data: vals,
        backgroundColor: colors,
        borderColor: colors,
        borderWidth: 1,
        borderRadius: 4
      }];
      customLegend = {
        display: true,
        position: "top",
        labels: {
          boxWidth: 12,
          padding: 12,
          generateLabels(chart) {
            return items.map((x, i) => {
              const visible = chart.getDataVisibility(i);
              return {
                text: `${truncateLabel(x.label, 18)}: ${formatCompactNumber(x.val)}`,
                fillStyle: colors[i],
                strokeStyle: colors[i],
                fontColor: visible ? "#334155" : "#94a3b8",
                hidden: !visible,
                index: i
              };
            });
          }
        },
        onClick(_e, legendItem, legend) {
          legend.chart.toggleDataVisibility(legendItem.index);
          legend.chart.update();
        }
      };
    }
  } else if (metricVal === "__grouped__") {
    isSingleSeries = false;
    const seriesCols = summaryCols.slice(0, 6);
    if (summaryCols.length > 6) {
      notes.push(`Showing the first 6 of ${summaryCols.length} numeric metrics for legibility.`);
    }
    const aggregated = aggregateByCategory(result.rows, labelCol, seriesCols);
    if (aggregated.length < result.rows.length) {
      notes.push(`Aggregated ${result.rows.length} rows into ${aggregated.length} unique ${labelCol.label} values.`);
    }
    const items = limit > 0 && aggregated.length > limit ? aggregated.slice(0, limit) : aggregated;
    fullLabels = items.map(x => x.label);
    const limitSuffix = limit > 0 && aggregated.length > limit ? ` (Top ${limit})` : "";
    chartTitle = `Metric Comparison by ${labelCol.label}${limitSuffix}`;
    categoryAxisTitle = labelCol.label;
    valueAxisTitle = "Value";

    datasets = seriesCols.map((col, idx) => {
      const color = CHART_PALETTE[idx % CHART_PALETTE.length];
      const vals = items.map(x => x.vals[idx]);
      return type === "line"
        ? {
          label: col.label,
          data: vals,
          borderColor: color,
          backgroundColor: color,
          pointBackgroundColor: color,
          pointBorderColor: "#ffffff",
          pointRadius: 4,
          borderWidth: 2.2,
          tension: 0.25,
          fill: false
        }
        : {
          label: col.label,
          data: vals,
          backgroundColor: color,
          borderColor: color,
          borderWidth: 1,
          borderRadius: 3
        };
    });
  } else {
    const metricCol = metricCols.find(c => String(c.i) === metricVal) || metricCols[0];
    const aggregated = aggregateByCategory(result.rows, labelCol, [metricCol]);
    if (aggregated.length < result.rows.length) {
      notes.push(`Summed ${result.rows.length} rows into ${aggregated.length} unique ${labelCol.label} values.`);
    }

    categoryAxisTitle = labelCol.label;
    valueAxisTitle = metricCol.label;

    if (isRadial) {
      const positive = aggregated.filter(x => x.vals[0] > 0);
      const omitted = aggregated.length - positive.length;
      if (omitted > 0) {
        notes.push(`${omitted} zero/negative categor${omitted === 1 ? "y" : "ies"} omitted from ${type === "pie" ? "Pie" : "Doughnut"}. Switch to Bar chart to view them.`);
      }
      if (positive.length === 0) {
        showChartNote("No positive values available for Pie/Doughnut. Switch to Bar chart.");
        return;
      }
      const cap = limit > 0 ? limit : 12;
      let slices;
      if (positive.length > cap) {
        const top = positive.slice(0, cap).map(x => ({ label: x.label, val: x.vals[0], isOther: false }));
        const rest = positive.slice(cap).reduce((acc, x) => acc + x.vals[0], 0);
        if (rest > 0) top.push({ label: `Other (${positive.length - cap})`, val: rest, isOther: true });
        slices = top;
      } else {
        slices = positive.map(x => ({ label: x.label, val: x.vals[0], isOther: false }));
      }
      fullLabels = slices.map(s => s.label);
      const vals = slices.map(s => s.val);
      const colors = slices.map((s, i) => s.isOther ? OTHER_SLICE_COLOR : CHART_PALETTE[i % CHART_PALETTE.length]);
      positiveSumForPct = vals.reduce((a, b) => a + b, 0);
      chartTitle = `${metricCol.label} Share by ${labelCol.label}${positive.length > cap ? ` (Top ${cap})` : ""}`;
      datasets = [{
        label: metricCol.label,
        data: vals,
        backgroundColor: colors,
        borderColor: "#ffffff",
        borderWidth: 2
      }];
      customLegend = buildRadialLegendPlugin(fullLabels, vals, colors, positiveSumForPct);
    } else {
      const items = limit > 0 && aggregated.length > limit ? aggregated.slice(0, limit) : aggregated;
      fullLabels = items.map(x => x.label);
      const vals = items.map(x => x.vals[0]);
      const limitSuffix = limit > 0 && aggregated.length > limit ? ` (Top ${limit})` : "";
      chartTitle = `${metricCol.label} by ${labelCol.label}${limitSuffix}`;
      positiveSumForPct = vals.filter(v => v > 0).reduce((a, b) => a + b, 0);

      if (type === "line") {
        if (!labelCol.isDate) {
          notes.push("Non-date X-axis is ordered by value descending. Select a Date column for chronological trends.");
        }
        datasets = [{
          label: metricCol.label,
          data: vals,
          borderColor: "#f15a24",
          backgroundColor: "rgba(241, 90, 36, 0.12)",
          pointBackgroundColor: "#f15a24",
          pointBorderColor: "#ffffff",
          pointRadius: 4.5,
          borderWidth: 2.5,
          tension: 0.25,
          fill: true
        }];
      } else {
        const hasPos = vals.some(v => v > 0);
        const hasNeg = vals.some(v => v < 0);
        if (hasPos && hasNeg) {
          isSingleSeries = false;
          datasets = [
            {
              label: `${metricCol.label} (Positive)`,
              data: vals.map(v => v >= 0 ? v : null),
              backgroundColor: "#2563eb",
              borderColor: "#2563eb",
              borderWidth: 1,
              borderRadius: 4,
              grouped: false
            },
            {
              label: `${metricCol.label} (Negative)`,
              data: vals.map(v => v < 0 ? v : null),
              backgroundColor: "#dc2626",
              borderColor: "#dc2626",
              borderWidth: 1,
              borderRadius: 4,
              grouped: false
            }
          ];
        } else {
          const barColor = hasNeg ? "#dc2626" : "#2563eb";
          const plottedSum = vals.reduce((a, b) => a + b, 0);
          datasets = [{
            label: `${metricCol.label} (Total: ${formatCompactNumber(plottedSum)})`,
            data: vals,
            backgroundColor: barColor,
            borderColor: barColor,
            borderWidth: 1,
            borderRadius: 4
          }];
        }
      }
    }
  }

  const allPlottedVals = datasets.flatMap(ds => ds.data).filter(v => v !== null && v !== undefined && Number.isFinite(v));
  const canUseLog = wantLog && !isRadial && allPlottedVals.length > 0 && allPlottedVals.every(v => v > 0);
  if (wantLog && !isRadial && !canUseLog) {
    notes.push("Log scale requires all plotted values to be > 0; showing linear scale.");
  }

  const usesCategoryDim = metricVal !== "__summary__" && !(isRadial && metricVal === "__grouped__");
  if (usesCategoryDim && fullLabels.length === 1 && result.rows.length > 1) {
    const alt = pickDefaultDimension(dimensionCols.filter(c => c.i !== labelCol.i), type);
    if (alt && alt.distinctCount >= 2) {
      notes.push(`All ${result.rows.length} rows share 1 '${labelCol.label}' value. Switch Category / X-Axis to '${alt.label}' (${alt.distinctCount} values) to break down this chart.`);
    }
  }

  if (isSingleSeries && !isRadial && positiveSumForPct > 0 && allPlottedVals.every(v => v >= 0) && allPlottedVals.some(v => v / positiveSumForPct > 0.95)) {
    notes.push("One item accounts for >95% of the total. Enable 'Log scale' or inspect Line Details for smaller values.");
  }

  if (notes.length > 0) showChartNote(notes.join(" "));

  const chartWrap = $("reportChart")?.parentElement;
  if (chartWrap?.style) {
    chartWrap.style.height = isHorizontal && fullLabels.length > 12
      ? `${Math.min(720, Math.max(440, fullLabels.length * 30 + 110))}px`
      : "440px";
  }

  const displayLabels = fullLabels.map(l => truncateLabel(l, isRadial ? 34 : (isHorizontal ? 30 : 22)));

  const categoryAxis = {
    title: { display: true, text: categoryAxisTitle, font: { weight: "600", size: 12 }, color: "#334155", padding: { top: 8, bottom: 4 } },
    ticks: {
      autoSkip: fullLabels.length > (isHorizontal ? 30 : 20),
      maxRotation: isHorizontal ? 0 : 40,
      minRotation: !isHorizontal && fullLabels.length > 6 ? 25 : 0,
      color: "#334155",
      font: { size: 11 }
    },
    grid: { display: isHorizontal, color: "#f1f5f9" }
  };
  const valueAxis = {
    type: canUseLog ? "logarithmic" : "linear",
    beginAtZero: !canUseLog && allPlottedVals.every(v => v >= 0),
    title: { display: true, text: valueAxisTitle, font: { weight: "600", size: 12 }, color: "#334155", padding: { top: 4, bottom: 8 } },
    ticks: { callback: val => formatCompactNumber(val), color: "#334155", font: { size: 11 } },
    grid: { color: "#e2e8f0" }
  };
  const scales = isRadial
    ? undefined
    : (isHorizontal ? { x: valueAxis, y: categoryAxis } : { x: categoryAxis, y: valueAxis });

  state.chart = new Chart($("reportChart"), {
    type: chartJsType,
    data: { labels: displayLabels, datasets },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      indexAxis: isHorizontal ? "y" : "x",
      interaction: type === "line" ? { mode: "index", intersect: false } : undefined,
      scales,
      plugins: {
        title: {
          display: true,
          text: chartTitle,
          color: "#0f172a",
          font: { size: 14, weight: "600" },
          padding: { bottom: 10 }
        },
        legend: customLegend,
        tooltip: {
          callbacks: {
            title(items) {
              if (!items?.length) return "";
              const idx = items[0].dataIndex;
              return fullLabels[idx] ?? items[0].label ?? "";
            },
            label(ctx) {
              const rawVal = isRadial ? ctx.parsed : (isHorizontal ? ctx.parsed.x : ctx.parsed.y);
              const numVal = Number(rawVal);
              const formatted = fmtVal(numVal);
              const dsLabel = ctx.dataset?.label || "";
              if ((isRadial || isSingleSeries) && positiveSumForPct > 0 && numVal > 0) {
                const pct = ((numVal / positiveSumForPct) * 100).toFixed(1);
                return `${dsLabel ? `${dsLabel}: ` : ""}${formatted} (${pct}%)`;
              }
              return `${dsLabel ? `${dsLabel}: ` : ""}${formatted}`;
            }
          }
        }
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

async function exportGroupPackPdf() {
  const btn = $("exportGroupPdfBtn");
  const prev = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Building PDF...";
  try {
    const isAll = !state.company || state.company.toUpperCase() === "ALL";
    const url = isAll
      ? "/api/reports/export-group-pack"
      : `/api/reports/export-company-pack?company=${encodeURIComponent(state.company)}`;
    const fileName = isAll
      ? "TERO_CrossCompany_Group_Reports.pdf"
      : `TERO_SingleCompany_Reports_${state.company}.pdf`;
    const res = await fetch(url, { method: "POST" });
    if (!res.ok) throw new Error("PDF Report Pack export failed.");
    const blob = await res.blob();
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = fileName;
    a.click();
  } catch (e) {
    alert(e.message || "Export failed.");
  } finally {
    btn.disabled = false;
    btn.textContent = prev;
  }
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
$("chartTypeSelect").onchange = () => {
  if (!state.lastResult) return;
  if ($("chartTypeSelect").value === "line" && $("chartLabelSelect")) {
    const { dimensionCols } = classifyColumns(state.lastResult);
    const dtCol = dimensionCols.find(c => c.isDate && c.distinctCount >= 2);
    if (dtCol) $("chartLabelSelect").value = String(dtCol.i);
  }
  renderChart(state.lastResult);
};
if ($("chartLabelSelect")) $("chartLabelSelect").onchange = () => state.lastResult && renderChart(state.lastResult);
if ($("chartMetricSelect")) $("chartMetricSelect").onchange = () => state.lastResult && renderChart(state.lastResult);
if ($("chartLimitSelect")) $("chartLimitSelect").onchange = () => state.lastResult && renderChart(state.lastResult);
if ($("chartLogScale")) $("chartLogScale").onchange = () => state.lastResult && renderChart(state.lastResult);

document.querySelectorAll(".chip").forEach(c => c.onclick = () => { $("aiPrompt").value = c.dataset.prompt; });
document.querySelectorAll(".export-btn").forEach(b => b.onclick = () => exportReport(b.dataset.fmt));
if ($("exportGroupPdfBtn")) $("exportGroupPdfBtn").onclick = () => exportGroupPackPdf();
document.querySelectorAll(".tab").forEach(t => t.onclick = () => {
  document.querySelectorAll(".tab").forEach(x => x.classList.remove("active"));
  t.classList.add("active");
  $("tableTab").classList.toggle("hidden", t.dataset.tab !== "table");
  $("chartTab").classList.toggle("hidden", t.dataset.tab !== "chart");
  $("sqlTab").classList.toggle("hidden", t.dataset.tab !== "sql");
  if (t.dataset.tab === "chart" && state.lastResult) {
    renderChart(state.lastResult);
    state.chartDirty = false;
  }
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
      body: JSON.stringify({
        question: q,
        companyCode: state.company,
        forceRefresh: $("aiBypassCache")?.checked || false
      })
    });
    if (res.isOutOfScope || res.guardrailBlocked || res.missingDataNote) {
      const prefix = res.isOutOfScope ? "Out of Scope: " : (res.guardrailBlocked ? "Guardrail: " : "");
      const msg = res.missingDataNote || res.explanation || "Request is outside permitted ERP reporting scope.";
      $("aiNotice").textContent = prefix && !msg.startsWith(prefix) && !msg.startsWith("Out of scope") && !msg.startsWith("Request blocked")
        ? prefix + msg
        : msg;
      $("aiNotice").className = "alert alert-error";
      return;
    }
    const cachePrefix = res.fromCache ? `[Cached (${res.cacheSource || "Memory"}) — 0 tokens] ` : "";
    if (res.isReconcile) {
      $("aiNotice").textContent = cachePrefix + (res.explanation || "Routing to deterministic GL / Trial Balance reconciler...");
      $("aiNotice").className = "alert";
      if (autoRun) await runReconcile(q, res.reconcileAreas);
      return;
    }
    if (res.request) {
      applyAiRequest(res.request);
      const baseNote = res.explanation || "Configuration populated below. Review fields or click Generate Report.";
      const warnNote = res.guardrailWarnings?.length ? ` (${res.guardrailWarnings.join(" ")})` : "";
      $("aiNotice").textContent = cachePrefix + baseNote + warnNote;
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
if ($("saveReportBtn")) $("saveReportBtn").onclick = () => saveCurrentReport();

async function loadSavedReports() {
  try {
    state.savedReports = await api("/api/reports/saved");
    renderSavedReports();
  } catch { /* ignore on signed-out state */ }
}

function renderSavedReports() {
  const el = $("savedReportsList");
  if (!el) return;
  if (!state.savedReports?.length) {
    el.innerHTML = `<span class="muted">No saved reports yet. Configure a report below and click Save Report.</span>`;
    return;
  }
  el.innerHTML = state.savedReports.map(r => `
    <div class="saved-item">
      <div class="saved-head">
        <span class="saved-title">${escHtml(r.name)}</span>
        ${r.isShared ? `<span class="pill-shared">Shared</span>` : ""}
      </div>
      <div class="saved-meta">
        <span>${escHtml(r.companyCode)}</span>
        <span>rpt.${escHtml(r.viewName)}</span>
        <span>Runs: ${r.runCount}</span>
      </div>
      <div class="saved-actions">
        <button type="button" class="btn btn-tero saved-run-btn" style="padding:4px 10px;font-size:.76rem;" data-id="${r.reportId}">Run (0 tokens)</button>
        <button type="button" class="btn btn-toolbar saved-load-btn" style="padding:4px 10px;font-size:.76rem;" data-id="${r.reportId}">Load</button>
        <button type="button" class="btn btn-link saved-del-btn" style="margin-left:auto;" data-id="${r.reportId}">Delete</button>
      </div>
    </div>`).join("");

  el.querySelectorAll(".saved-run-btn").forEach(b => b.onclick = () => runSavedReport(Number(b.dataset.id)));
  el.querySelectorAll(".saved-load-btn").forEach(b => b.onclick = () => loadSavedReportToForm(Number(b.dataset.id)));
  el.querySelectorAll(".saved-del-btn").forEach(b => b.onclick = () => deleteSavedReport(Number(b.dataset.id)));
}

async function saveCurrentReport() {
  const nameEl = $("saveReportName");
  const name = (nameEl?.value || "").trim();
  if (!name) {
    $("aiNotice").textContent = "Enter a name to save this report for 0-token replay.";
    $("aiNotice").className = "alert alert-error";
    return;
  }
  const req = collectRequest();
  try {
    await api("/api/reports/saved", {
      method: "POST",
      body: JSON.stringify({
        name,
        description: "",
        isShared: $("saveReportShared")?.checked || false,
        promptText: $("aiPrompt")?.value.trim() || "",
        request: req
      })
    });
    if (nameEl) nameEl.value = "";
    $("aiNotice").textContent = `Saved '${name}' for 0-token replay.`;
    $("aiNotice").className = "alert";
    await loadSavedReports();
  } catch (e) {
    $("aiNotice").textContent = e.message;
    $("aiNotice").className = "alert alert-error";
  }
}

async function loadSavedReportToForm(id) {
  const item = state.savedReports.find(x => x.reportId === id);
  if (!item) return;
  try {
    const req = JSON.parse(item.requestJson);
    if (req.companyCode && req.companyCode !== state.company && [...$("companySelect").options].some(o => o.value === req.companyCode)) {
      $("companySelect").value = req.companyCode;
      await loadAreasForCompany(req.companyCode);
    }
    applyAiRequest(req);
    if (item.promptText) $("aiPrompt").value = item.promptText;
    $("aiNotice").textContent = `Loaded saved report '${item.name}' (0 tokens). Click Generate Report or adjust filters.`;
    $("aiNotice").className = "alert";
  } catch (e) {
    $("aiNotice").textContent = e.message || "Could not load saved report.";
    $("aiNotice").className = "alert alert-error";
  }
}

async function runSavedReport(id) {
  const item = state.savedReports.find(x => x.reportId === id);
  try {
    await loadSavedReportToForm(id);
    const data = await api(`/api/reports/saved/${id}/run`, { method: "POST" });
    state.lastRequest = item ? JSON.parse(item.requestJson) : collectRequest();
    state.lastResult = data.result;
    $("resultTitle").textContent = `${data.displayName} (${data.companyCode})`;
    $("resultMeta").textContent = `Rows: ${data.result.rows.length}${data.result.truncated ? " (truncated)" : ""} | Audit Run #${data.runId} | Saved Replay (0 tokens)`;
    $("sqlPreview").textContent = data.sql;
    $("viewToggle").classList.remove("hidden");
    document.querySelectorAll(".export-btn").forEach(b => b.disabled = false);
    populateGroupSelect(data.result);
    renderTable(data.result);
    syncAndRenderChart(data.result, data.chartType);
    await loadSavedReports();
  } catch (e) {
    $("aiNotice").textContent = e.message || "Could not run saved report.";
    $("aiNotice").className = "alert alert-error";
  }
}

async function deleteSavedReport(id) {
  try {
    await api(`/api/reports/saved/${id}`, { method: "DELETE" });
    await loadSavedReports();
  } catch (e) {
    $("aiNotice").textContent = e.message || "Could not delete saved report.";
    $("aiNotice").className = "alert alert-error";
  }
}

// Admin RBAC modal
$("adminBtn").onclick = async () => {
  $("adminModal").classList.remove("hidden");
  await Promise.all([refreshRbacTable(), refreshCacheStats()]);
};
if ($("clearAiCacheBtn")) {
  $("clearAiCacheBtn").onclick = async () => {
    await api("/api/admin/ai-cache", { method: "DELETE" });
    await refreshCacheStats();
  };
}
async function refreshCacheStats() {
  if (!$("cacheStatsText")) return;
  try {
    const s = await api("/api/admin/ai-cache/stats");
    $("cacheStatsText").textContent = `AI Plan Cache — Entries: ${s.memoryEntries} | Memory Hits: ${s.memoryHits} | DB Hits: ${s.databaseHits} | Misses: ${s.misses}`;
  } catch {
    $("cacheStatsText").textContent = "AI Plan Cache stats unavailable.";
  }
}
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


// ---- Voice input (browser Web Speech API) -> fills the same prompt box used for typing, so the same AI planner, RBAC and guardrails apply ----
(function initVoice() {
  const btn = $("voiceBtn");
  if (!btn) return;
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  const status = $("voiceStatus"), label = $("voiceLabel"), prompt = $("aiPrompt");
  const autoWrap = $("voiceAutoRun")?.closest("label");
  if (!SR) {
    btn.hidden = true;
    if (autoWrap) autoWrap.hidden = true;
    status.textContent = "Voice input is not supported in this browser. Use Chrome, Edge or Safari.";
    return;
  }

  let rec = null, listening = false, base = "", finalText = "", heardSpeech = false;

  const setListening = (on) => {
    listening = on;
    btn.classList.toggle("listening", on);
    btn.setAttribute("aria-pressed", String(on));
    btn.setAttribute("aria-label", on ? "Stop voice input" : "Start voice input");
    label.textContent = on ? "Listening... click to stop" : "Speak";
  };

  function start() {
    rec = new SR();
    rec.lang = navigator.language || "en-US";
    rec.interimResults = true;
    rec.continuous = false;
    base = prompt.value.trim();
    finalText = "";
    heardSpeech = false;
    status.textContent = "";

    rec.onstart = () => setListening(true);
    rec.onresult = (ev) => {
      let interim = "";
      for (let i = ev.resultIndex; i < ev.results.length; i++) {
        const t = ev.results[i][0].transcript;
        if (ev.results[i].isFinal) finalText += t; else interim += t;
      }
      heardSpeech = true;
      prompt.value = [base, (finalText + interim).trim()].filter(Boolean).join(" ");
    };
    rec.onerror = (ev) => {
      const msgs = {
        "not-allowed": "Microphone access was blocked. Allow it in the browser address bar and try again.",
        "service-not-allowed": "Microphone access was blocked. Allow it in the browser address bar and try again.",
        "no-speech": "I did not hear anything. Click Speak and try again.",
        "audio-capture": "No microphone found.",
        "network": "Speech service unreachable. Check your connection, or type the request.",
        "aborted": ""
      };
      status.textContent = msgs[ev.error] ?? `Voice error: ${ev.error}`;
    };
    rec.onend = () => {
      setListening(false);
      if (heardSpeech && prompt.value.trim()) {
        status.textContent = "Heard it. Review the text, then run.";
        if ($("voiceAutoRun")?.checked) handleAi(true);
      }
    };
    try { rec.start(); } catch { setListening(false); }
  }

  btn.onclick = () => { if (listening) rec.stop(); else start(); };
})();
