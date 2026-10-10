// The Exchange Mini App (spec 1.7): four tabs over the app's API.
//
// Rules this file keeps (FrontendScanTests checks the first three, INV-18):
// - nothing is loaded from anywhere but telegram.org;
// - the access token lives in this module's memory only (D-003), never in any storage;
// - every text the client reads comes from the locale file, through the lookup function t;
// - money stays a string end to end (RULE-MONEY-DECIMAL): amounts are never made numbers;
// - the DOM is built with textContent only, so nothing a client typed can become markup.

const QUOTE_DEBOUNCE_MS = 400;
const QUOTE_REFRESH_MS = 60_000;
const POINTS_FOR_TOP_TIER = 10;
// Spec 1.6: at least 6 points gives 1 %, at least 8 gives 2 %, 10 gives 3 %. Shown only: the
// server applies no discount until PHASE-6, which will make it the source of these tiers.
const DISCOUNT_TIERS = [
  { points: 10, percent: "3" },
  { points: 8, percent: "2" },
  { points: 6, percent: "1" },
];
const AMOUNT_PATTERN = /^(0|[1-9][0-9]{0,15})([.,][0-9]{1,2})?$/;
const GAUGE_RADIUS = 80;
const GAUGE_MARGIN = 20;
const GAUGE_STROKE = 14;
const THIN_SPACE = String.fromCharCode(0x2009);
// The API lists a direction's fields alphabetically; the form shows them in reading order:
// the giving side first, then where the money arrives. A field not named here goes last.
const FIELD_ORDER = [
  "bank_give_id",
  "network_give_id",
  "usdt_transfer_time",
  "city_id",
  "bank_receive_id",
  "network_receive_id",
  "iban",
  "recipient_name",
  "wallet_address",
  "payment_purpose",
];
const HTTP_UNAUTHORIZED = 401;
const HTTP_NOT_FOUND = 404;
const HTTP_UNPROCESSABLE = 422;
const HTTP_SERVICE_UNAVAILABLE = 503;
// What api() reports when the request never got an answer.
const NO_RESPONSE = 0;

const tg = window.Telegram ? window.Telegram.WebApp : null;
const state = {
  token: null,
  // Spec 1.9: opened outside Telegram, against an app in demo mode.
  demo: false,
  texts: {},
  directions: [],
  cities: [],
  banks: [],
  networks: [],
  tab: "home",
  form: { give: "RUB_WIRE", receive: "EUR_CASH", amount_give: "" },
  quote: null,
  quoteError: null,
  quotedAt: null,
  // Counts quote requests; only the answer to the latest one is shown.
  quoteRequest: 0,
};
let quoteTimer = null;
let refreshTimer = null;

// --- text and DOM ------------------------------------------------------------------------------

function t(key) {
  return state.texts[key] || key;
}

function el(tag, attributes = {}, children = []) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (name === "text") node.textContent = value;
    else if (name.startsWith("on")) node.addEventListener(name.slice(2), value);
    else if (value === true) node.setAttribute(name, "");
    else if (value !== false && value !== null && value !== undefined) node.setAttribute(name, value);
  }
  for (const child of [].concat(children)) {
    if (child !== null && child !== undefined) node.append(child);
  }
  return node;
}

function show(...nodes) {
  document.getElementById("screen").replaceChildren(...nodes);
}

// Thousands separated by a thin space; done on the string, so no precision is ever lost.
function money(amount) {
  const [whole, fraction] = String(amount).split(".");
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, THIN_SPACE);
  return fraction ? `${grouped}.${fraction}` : grouped;
}

function trimZeros(decimal) {
  const text = String(decimal);
  return text.includes(".") ? text.replace(/0+$/, "").replace(/\.$/, "") : text;
}

function codeName(side, code) {
  return side === "give"
    ? t("give_" + code) // locale-dynamic: give_
    : t("receive_" + code); // locale-dynamic: receive_
}

function statusName(status) {
  return t("status_" + status); // locale-dynamic: status_
}

// --- the API -----------------------------------------------------------------------------------

async function signIn() {
  const response = state.demo
    ? await fetch("/api/auth/demo", { method: "POST" })
    : await fetch("/api/auth/telegram", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ init_data: tg.initData }),
      });
  if (!response.ok) throw new Error("sign-in refused");
  state.token = (await response.json()).access_token;
}

// Requests that meet an expired token share one sign-in.
let signingIn = null;

function signInOnce() {
  if (!signingIn) signingIn = signIn().finally(() => (signingIn = null));
  return signingIn;
}

// One silent re-sign-in when the 60-minute token has expired (D-003). Never throws: a request that
// got no answer, or whose re-sign-in failed, comes back as { ok: false, status: NO_RESPONSE }.
async function api(path, options = {}, retried = false) {
  const headers = { "Content-Type": "application/json" };
  if (state.token) headers.Authorization = `Bearer ${state.token}`;
  let response;
  try {
    response = await fetch(path, { ...options, headers });
    if (response.status === HTTP_UNAUTHORIZED && !retried) {
      await signInOnce();
      return api(path, options, true);
    }
  } catch {
    return { ok: false, status: NO_RESPONSE, body: null };
  }
  let body = null;
  try {
    body = await response.json();
  } catch {
    body = null;
  }
  return { ok: response.ok, status: response.status, body };
}

// --- start -------------------------------------------------------------------------------------

async function start() {
  state.texts = await (await fetch("/static/locale/ru.json")).json();
  document.title = t("app_title");
  const inTelegram = Boolean(tg && tg.initData);
  if (inTelegram) {
    tg.ready();
    tg.expand();
  } else {
    const demo = await startDemo();
    if (demo === null) return;
    if (!demo) {
      show(el("div", { class: "card" }, [
        el("h1", { text: t("app_title") }),
        el("p", { text: t("open_in_telegram") }),
      ]));
      return;
    }
  }
  try {
    if (inTelegram) await signIn();
    const [directions, cities, banks, networks] = await Promise.all([
      api("/api/directions"),
      api("/api/cities"),
      api("/api/banks"),
      api("/api/usdt-networks"),
    ]);
    if (![directions, cities, banks, networks].every((result) => result.ok)) throw new Error("load");
    state.directions = directions.body;
    state.cities = cities.body;
    state.banks = banks.body;
    state.networks = networks.body;
  } catch {
    show(el("div", { class: "card" }, [el("p", { class: "error", text: t("load_failed") })]));
    return;
  }
  renderTabs();
  go("home");
}

// Outside Telegram the page signs in to the demo, if the app runs one (spec 1.9); anywhere else
// the route is absent and the page asks to be opened from Telegram.
async function startDemo() {
  let response;
  try {
    response = await fetch("/api/auth/demo", { method: "POST" });
  } catch {
    response = null;
  }
  if (!response || response.status === HTTP_NOT_FOUND) return false;
  if (!response.ok) {
    // The app runs a demo but cannot sign in now (its database, say): not a reason to send the
    // visitor to Telegram.
    show(el("div", { class: "card" }, [el("p", { class: "error", text: t("load_failed") })]));
    return null;
  }
  try {
    state.token = (await response.json()).access_token;
    if (!state.token) throw new Error("no token");
  } catch {
    show(el("div", { class: "card" }, [el("p", { class: "error", text: t("load_failed") })]));
    return null;
  }
  state.demo = true;
  const banner = document.getElementById("banner");
  banner.textContent = t("demo_banner");
  banner.hidden = false;
  return true;
}

// Telegram's dialogs inside Telegram; the browser's own in the demo, where no Telegram answers.
function askConfirm(text) {
  if (!state.demo && tg && tg.showConfirm) {
    return new Promise((resolve) => tg.showConfirm(text, resolve));
  }
  return Promise.resolve(window.confirm(text));
}

function tell(text) {
  if (!state.demo && tg && tg.showAlert) tg.showAlert(text);
  else window.alert(text);
}

// --- tabs --------------------------------------------------------------------------------------

const TABS = [
  { id: "home", render: () => renderHome() },
  { id: "exchange", render: () => renderExchange() },
  { id: "privileges", render: () => renderPrivileges() },
  { id: "profile", render: () => renderProfile() },
];

function tabLabel(id) {
  return t("tab_" + id); // locale-dynamic: tab_
}

function renderTabs() {
  const nav = document.getElementById("tabs");
  nav.replaceChildren(
    ...TABS.map((tab) =>
      el("button", { "data-tab": tab.id, text: tabLabel(tab.id), onclick: () => go(tab.id) }),
    ),
  );
  nav.hidden = false;
}

function go(tab) {
  state.tab = tab;
  for (const button of document.querySelectorAll("#tabs button")) {
    if (button.dataset.tab === tab) button.setAttribute("aria-current", "page");
    else button.removeAttribute("aria-current");
  }
  stopQuoting();
  TABS.find((item) => item.id === tab).render();
}

// --- Home --------------------------------------------------------------------------------------

function renderHome() {
  show(
    el("h1", { text: t("home_title") }),
    el("p", { class: "hint", text: t("home_lead") }),
    el("div", { class: "card" }, [
      el("p", { text: t("home_step_1") }),
      el("p", { text: t("home_step_2") }),
      el("p", { text: t("home_step_3") }),
    ]),
    el("button", { text: t("home_start"), onclick: () => go("exchange") }),
  );
}

// --- Exchange ----------------------------------------------------------------------------------

function directionOf(give, receive) {
  return state.directions.find((d) => d.give === give && d.receive === receive);
}

function receiveOptions(give) {
  return state.directions.filter((d) => d.give === give).map((d) => d.receive);
}

function gives() {
  return [...new Set(state.directions.map((d) => d.give))];
}

function choicesFor(field) {
  if (field === "city_id") return state.cities.map((c) => [c.id, c.name]);
  if (field === "bank_give_id") {
    return state.banks
      .filter((b) => b.channel === "WIRE" && b.currency === "RUB" && b.country === "RU")
      .map((b) => [b.id, b.code]);
  }
  if (field === "bank_receive_id") {
    return state.banks
      .filter((b) => b.channel === "ATM" && b.currency === "EUR" && b.country === "ME")
      .map((b) => [b.id, b.code]);
  }
  if (field === "network_give_id" || field === "network_receive_id") {
    return state.networks.map((n) => [n.id, n.code]);
  }
  if (field === "usdt_transfer_time") {
    return ["now", "at_meeting"].map((value) => [value, t("transfer_" + value)]); // locale-dynamic: transfer_
  }
  return null;
}

function fieldLabel(field) {
  return t("field_" + field); // locale-dynamic: field_
}

function select(name, options, value, onchange) {
  return el(
    "select",
    { name, onchange: (event) => onchange(event.target.value) },
    options.map(([optionValue, text]) =>
      el("option", { value: optionValue, text, selected: String(optionValue) === String(value) }),
    ),
  );
}

function fieldInput(field, direction) {
  const choices = choicesFor(field);
  const value = state.form[field] ?? "";
  const update = (next) => {
    state.form[field] = next;
    if (direction.quote_fields.includes(field)) formChanged();
    else refreshPlaceButton();
  };
  const control = choices
    ? select(field, [["", t("choose")], ...choices], value, update)
    : el("input", {
        name: field,
        value,
        autocomplete: "off",
        oninput: (event) => update(event.target.value),
      });
  return el("div", { id: `field-${field}` }, [el("label", { text: fieldLabel(field) }), control]);
}

function renderExchange() {
  stopQuoting();
  const { give, receive } = state.form;
  if (!receiveOptions(give).includes(receive)) state.form.receive = receiveOptions(give)[0];
  const direction = directionOf(state.form.give, state.form.receive);
  const fields = inFormOrder([...direction.order_fields, ...direction.optional_fields]);
  show(
    el("h1", { text: t("exchange_title") }),
    el("label", { text: t("field_amount_give") }),
    el("input", {
      name: "amount_give",
      inputmode: "decimal",
      value: state.form.amount_give,
      placeholder: "1000",
      oninput: (event) => {
        state.form.amount_give = event.target.value.trim();
        formChanged();
      },
    }),
    el("label", { text: t("field_give") }),
    select("give", gives().map((code) => [code, codeName("give", code)]), give, (next) => {
      state.form.give = next;
      dropQuote();
      renderExchange();
    }),
    el("label", { text: t("field_receive") }),
    select(
      "receive",
      receiveOptions(state.form.give).map((code) => [code, codeName("receive", code)]),
      state.form.receive,
      (next) => {
        state.form.receive = next;
        dropQuote();
        renderExchange();
      },
    ),
    ...fields.map((field) => fieldInput(field, direction)),
    el("label", { text: t("field_amount_receive") }),
    el("input", { id: "amount-receive", readonly: true, value: "" }),
    el("div", { id: "quote", class: "card" }),
    el("p", { id: "form-error", class: "error" }),
    el("button", { id: "place", text: t("place_order"), disabled: true, onclick: confirmOrder }),
  );
  renderQuote();
  scheduleQuote();
  refreshTimer = setInterval(() => requestQuote(), QUOTE_REFRESH_MS);
}

function inFormOrder(fields) {
  const rank = (field) => (FIELD_ORDER.includes(field) ? FIELD_ORDER.indexOf(field) : FIELD_ORDER.length);
  return [...fields].sort((a, b) => rank(a) - rank(b));
}

function amountForApi() {
  const amount = state.form.amount_give.replace(",", ".");
  return AMOUNT_PATTERN.test(state.form.amount_give) ? amount : null;
}

function quoteBody(direction) {
  const body = { give: state.form.give, receive: state.form.receive, amount_give: amountForApi() };
  for (const field of direction.quote_fields) {
    if (!state.form[field]) return null;
    body[field] = Number(state.form[field]); // a reference row id, never money
  }
  return body.amount_give ? body : null;
}

function scheduleQuote() {
  clearTimeout(quoteTimer);
  quoteTimer = setTimeout(requestQuote, QUOTE_DEBOUNCE_MS);
}

function stopQuoting() {
  clearTimeout(quoteTimer);
  clearInterval(refreshTimer);
}

// A quote belongs to the form it was asked for: any change drops it, so neither "Place order" nor
// the confirmation can show a number the order would not get, and a late answer is ignored.
function dropQuote() {
  state.quote = null;
  state.quoteError = null;
  state.quoteRequest += 1;
}

function formChanged() {
  dropQuote();
  renderQuote();
  scheduleQuote();
}

async function requestQuote() {
  const direction = directionOf(state.form.give, state.form.receive);
  const body = quoteBody(direction);
  state.quoteRequest += 1;
  const request = state.quoteRequest;
  if (!body) {
    state.quote = null;
    state.quoteError = null;
    renderQuote();
    return;
  }
  const result = await api("/api/rate", { method: "POST", body: JSON.stringify(body) });
  if (request !== state.quoteRequest) return;
  if (result.ok) {
    state.quote = result.body;
    state.quoteError = null;
    state.quotedAt = new Date();
  } else {
    state.quote = null;
    state.quoteError = result.status === HTTP_UNPROCESSABLE ? refusalText(result) : unavailableText(result);
    if (result.status === HTTP_UNPROCESSABLE) markFields(result.body && result.body.fields);
  }
  renderQuote();
}

function renderQuote() {
  const box = document.getElementById("quote");
  const receive = document.getElementById("amount-receive");
  if (!box) return;
  if (state.quoteError) {
    receive.value = "";
    box.replaceChildren(el("p", { class: "error", text: state.quoteError }));
  } else if (!state.quote) {
    receive.value = "";
    box.replaceChildren(el("p", { class: "hint", text: t("quote_needs_input") }));
  } else {
    const quote = state.quote;
    receive.value = `${money(quote.amount_receive)} ${codeName("receive", quote.receive)}`;
    const markups = Object.entries(quote.markup_percent).filter(
      ([name, value]) => !["total", "applied"].includes(name) && trimZeros(value) !== "0",
    );
    box.replaceChildren(
      el("div", { class: "row" }, [
        el("span", { text: t("rate") }),
        el("span", { text: trimZeros(quote.effective_rate) }),
      ]),
      ...markups.map(([name, value]) =>
        el("div", { class: "row hint" }, [
          el("span", { text: t("markup_" + name) }), // locale-dynamic: markup_
          el("span", { text: `${trimZeros(value)} %` }),
        ]),
      ),
      el("p", {
        class: "hint",
        text: `${t("quote_updated")} ${state.quotedAt.toLocaleTimeString("ru-RU")}`,
      }),
    );
  }
  refreshPlaceButton();
}

function orderComplete(direction) {
  return direction.order_fields.every((field) => state.form[field]);
}

function refreshPlaceButton() {
  const button = document.getElementById("place");
  if (!button) return;
  const direction = directionOf(state.form.give, state.form.receive);
  button.disabled = !(state.quote && quoteBody(direction) && orderComplete(direction));
}

function orderBody(direction) {
  const body = quoteBody(direction);
  for (const field of [...direction.order_fields, ...direction.optional_fields]) {
    const value = state.form[field];
    if (!value) continue;
    body[field] = field.endsWith("_id") ? Number(value) : value; // ids only, never money
  }
  return body;
}

function confirmOrder() {
  const quote = state.quote;
  const modal = document.getElementById("modal");
  const close = () => {
    modal.hidden = true;
    modal.replaceChildren();
  };
  modal.replaceChildren(
    el("div", { class: "card" }, [
      el("h2", { text: t("confirm_title") }),
      el("div", { class: "row" }, [
        el("span", { text: t("field_amount_give") }),
        el("span", { text: `${money(quote.amount_give)} ${codeName("give", quote.give)}` }),
      ]),
      el("div", { class: "row" }, [
        el("span", { text: t("field_amount_receive") }),
        el("span", {}, [
          el("div", { class: "big", text: money(quote.amount_receive) }),
          el("div", { class: "hint", text: codeName("receive", quote.receive) }),
        ]),
      ]),
      el("div", { class: "row hint" }, [
        el("span", { text: t("rate") }),
        el("span", { text: trimZeros(quote.effective_rate) }),
      ]),
      el("p", { id: "confirm-error", class: "error" }),
      el("button", { id: "confirm", text: t("confirm_place"), onclick: () => placeOrder(close) }),
      el("button", { class: "secondary", text: t("cancel"), onclick: close }),
    ]),
  );
  modal.hidden = false;
}

async function placeOrder(close) {
  const button = document.getElementById("confirm");
  button.disabled = true;
  const direction = directionOf(state.form.give, state.form.receive);
  if (!state.quote || !quoteBody(direction)) {
    close();
    return;
  }
  const result = await api("/api/orders", {
    method: "POST",
    body: JSON.stringify(orderBody(direction)),
  });
  if (!result.ok) {
    button.disabled = false;
    document.getElementById("confirm-error").textContent = refusalText(result);
    markFields(result.body && result.body.fields);
    return;
  }
  close();
  if (!state.demo && tg && tg.HapticFeedback) tg.HapticFeedback.notificationOccurred("success");
  state.form = { give: state.form.give, receive: state.form.receive, amount_give: "" };
  dropQuote();
  stopQuoting();
  show(
    el("div", { class: "card" }, [
      el("h1", { text: t("success_title") }),
      el("p", { text: `${t("success_order")} ${result.body.id}` }),
      el("p", { class: "hint", text: state.demo ? t("success_next_demo") : t("success_next") }),
    ]),
    el("button", { text: t("to_orders"), onclick: () => go("profile") }),
  );
}

// The server's own message when it gave one (a stale rate), else a plain "try again".
function unavailableText(result) {
  if (result.status === HTTP_SERVICE_UNAVAILABLE && result.body && result.body.message) {
    return result.body.message;
  }
  return result.status === NO_RESPONSE ? t("error_no_connection") : t("quote_failed");
}

function refusalText(result) {
  if (result.status !== HTTP_UNPROCESSABLE) return unavailableText(result);
  const kind = result.body && result.body.error;
  if (kind === "amount_out_of_range") return t("error_amount_out_of_range");
  if (kind === "amount_too_small") return t("error_amount_too_small");
  return t("error_check_fields");
}

function markFields(fields) {
  for (const node of document.querySelectorAll(".field-error")) node.classList.remove("field-error");
  for (const field of fields || []) {
    const name = field.replace(/^body\./, "");
    const node = document.getElementById(`field-${name}`);
    if (node) node.classList.add("field-error");
  }
}

// --- Privileges --------------------------------------------------------------------------------

function discountFor(points) {
  const tier = DISCOUNT_TIERS.find((item) => points >= item.points);
  return tier ? tier.percent : "0";
}

async function renderPrivileges() {
  const me = await api("/api/users/me");
  if (state.tab !== "privileges") return;
  if (!me.ok) {
    show(el("p", { class: "error", text: t("load_failed") }));
    return;
  }
  const points = me.body.loyalty_points;
  show(
    el("h1", { text: t("privileges_title") }),
    el("p", { class: "hint", text: t("privileges_lead") }),
    el("div", { class: "card" }, [
      ...[...DISCOUNT_TIERS].reverse().map((tier) =>
        el("div", { class: "row" }, [
          el("span", { text: `${tier.points} ${t("points_short")}` }),
          el("span", { text: `${tier.percent} %` }),
        ]),
      ),
    ]),
    el("p", { text: `${t("your_points")}: ${points}` }),
  );
}

// --- Profile -----------------------------------------------------------------------------------

function gauge(points) {
  // A semicircle from left to right; the value arc covers points / 10 of it.
  const share = Math.min(points, POINTS_FOR_TOP_TIER) / POINTS_FOR_TOP_TIER;
  const length = Math.PI * GAUGE_RADIUS;
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  const centre = GAUGE_RADIUS + GAUGE_MARGIN;
  const width = 2 * centre;
  svg.setAttribute("viewBox", `0 0 ${width} ${centre + GAUGE_MARGIN / 2}`);
  svg.setAttribute("width", String(width));
  svg.setAttribute("class", "gauge");
  for (const [cls, dash] of [["track", length], ["value", share * length]]) {
    const path = document.createElementNS(ns, "path");
    path.setAttribute(
      "d",
      `M ${GAUGE_MARGIN} ${centre} A ${GAUGE_RADIUS} ${GAUGE_RADIUS} 0 0 1 ${width - GAUGE_MARGIN} ${centre}`,
    );
    path.setAttribute("fill", "none");
    path.setAttribute("stroke-width", String(GAUGE_STROKE));
    path.setAttribute("stroke-linecap", "round");
    path.setAttribute("class", cls);
    path.setAttribute("stroke-dasharray", `${dash} ${length}`);
    svg.append(path);
  }
  return svg;
}

function nextTierHint(points) {
  const next = [...DISCOUNT_TIERS].reverse().find((tier) => points < tier.points);
  if (!next) return t("tier_top");
  return `${t("tier_next_before")} ${next.points - points} ${t("tier_next_after")} ${next.percent} %`;
}

async function renderProfile() {
  const [me, orders] = await Promise.all([api("/api/users/me"), api("/api/orders")]);
  if (state.tab !== "profile") return;
  if (!me.ok || !orders.ok) {
    show(el("p", { class: "error", text: t("load_failed") }));
    return;
  }
  const points = me.body.loyalty_points;
  show(
    el("h1", { text: me.body.first_name || me.body.username || t("profile_title") }),
    el("div", { class: "card" }, [
      gauge(points),
      el("div", { class: "row" }, [
        el("span", { text: t("your_points") }),
        el("span", { text: `${points} / ${POINTS_FOR_TOP_TIER}` }),
      ]),
      el("div", { class: "row" }, [
        el("span", { text: t("your_discount") }),
        el("span", { text: `${discountFor(points)} %` }),
      ]),
      el("div", { class: "row" }, [
        el("span", { text: t("order_count") }),
        el("span", { text: String(orders.body.length) }),
      ]),
      el("p", { class: "hint", text: nextTierHint(points) }),
    ]),
    el("h2", { text: t("history_title") }),
    ...(orders.body.length
      ? orders.body.map(orderCard)
      : [el("p", { class: "hint", text: t("history_empty") })]),
  );
}

function orderCard(order) {
  return el("div", { class: "card" }, [
    el("div", { class: "row" }, [
      el("span", { text: `${t("order_number")} ${order.id}` }),
      el("span", { class: `chip ${order.status}`, text: statusName(order.status) }),
    ]),
    el("div", { class: "row" }, [
      el("span", { text: `${money(order.amount_give)} ${codeName("give", order.give)}` }),
      el("span", { text: `${money(order.amount_receive)} ${codeName("receive", order.receive)}` }),
    ]),
    el("p", { class: "hint", text: new Date(order.created_at).toLocaleString("ru-RU") }),
    order.status === "PENDING"
      ? el("button", {
          class: "danger small",
          text: t("cancel_order"),
          onclick: () => cancelOrder(order.id),
        })
      : null,
  ]);
}

async function cancelOrder(orderId) {
  if (!(await askConfirm(t("cancel_confirm")))) return;
  const result = await api(`/api/orders/${orderId}/cancel`, { method: "POST" });
  if (!result.ok) tell(t("cancel_failed"));
  renderProfile();
}

start();
