"""Real-browser check for night mode and the guest flow. Saves docs/screenshots/*.png.

Needs a running stack and: pip install playwright pillow && playwright install chromium
"""
import base64, io, os, sys
from pathlib import Path

from PIL import Image
from playwright.sync_api import sync_playwright

BASE_URL = f"http://localhost:{os.environ.get('DATABRIDGE_PORT', '8088')}"
SHOTS = Path(__file__).resolve().parents[1] / "docs" / "screenshots"
DESKTOP = {"width": 1440, "height": 900}
MOBILE = {"width": 390, "height": 844}
STORAGE_KEY = "databridge-theme"
PAGE_COLORS = {"light": "rgb(244, 247, 243)", "dark": "rgb(16, 24, 22)"}
TOGGLE_NAMES = {"light": "Switch to night mode", "dark": "Switch to light mode"}

failures = []
browser_errors = []


def check(name, passed, detail=""):
    print(f"{'PASS' if passed else 'FAIL'} {name}{f' ({detail})' if detail else ''}", flush=True)
    if not passed:
        failures.append(name)


def watch_errors(page):
    page.on("pageerror", lambda error: browser_errors.append(f"{page.url}: {error}"))
    page.on("console", lambda m: browser_errors.append(f"{page.url}: {m.text}") if m.type == "error" else None)
    page.on("response", lambda r: browser_errors.append(f"{r.status} {r.url}") if r.status >= 400 else None)


def new_page(browser, scheme="light", **options):
    context = browser.new_context(color_scheme=scheme, viewport=options.pop("viewport", DESKTOP), **options)
    page = context.new_page()
    watch_errors(page)
    return page


def theme(page):
    return page.evaluate("document.documentElement.getAttribute('data-theme')")


def saved(page):
    return page.evaluate(f"localStorage.getItem('{STORAGE_KEY}')")


def toggle(page, name=None):
    return page.get_by_role("button", name=name or TOGGLE_NAMES[theme(page)])


def set_theme(page, wanted, tap=False):
    if theme(page) != wanted:
        button = toggle(page)
        button.tap() if tap else button.click()
    assert theme(page) == wanted


def theme_is_applied(page, wanted):
    """The attribute, the painted colours, and the toggle's accessible name all agree."""
    background = page.evaluate("getComputedStyle(document.documentElement).backgroundColor")
    return theme(page) == wanted and background == PAGE_COLORS[wanted] and toggle(page, TOGGLE_NAMES[wanted]).is_visible()


def capture(page, name, scroll_y=0, tap=False, full_page=False):
    """Saves a matching light and dark pair: same session, viewport, and scroll position."""
    sizes = []
    for wanted in ("light", "dark"):
        set_theme(page, wanted, tap)
        page.mouse.move(0, 0)
        page.evaluate("y => { document.activeElement?.blur(); window.scrollTo(0, y); }", scroll_y)
        page.wait_for_timeout(250)
        path = SHOTS / f"{name}-{wanted}.png"
        page.screenshot(path=path, animations="disabled", full_page=full_page)
        with Image.open(path) as image:
            sizes.append((image.format, image.size, page.evaluate("window.scrollY")))
    check(f"screenshot pair {name}", sizes[0] == sizes[1] and sizes[0][0] == "PNG", str(sizes[0]))


AUDIT_JS = """
() => {
  const parse = c => { const m = c.match(/[\\d.]+/g).map(Number); return m.length > 3 ? m : [...m, 1]; };
  const over = (top, under) => { const a = top[3]; return [0,1,2].map(i => top[i]*a + under[i]*(1-a)).concat(1); };
  const lum = c => { const f = v => { v /= 255; return v <= .03928 ? v/12.92 : ((v+.055)/1.055)**2.4; };
                     return .2126*f(c[0]) + .7152*f(c[1]) + .0722*f(c[2]); };
  const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x+.05)/(y+.05); };
  const backdrop = element => {
    const layers = [];
    for (let e = element; e; e = e.parentElement) {
      const c = parse(getComputedStyle(e).backgroundColor);
      if (c[3] > 0) layers.push(c);
      if (c[3] === 1) break;
    }
    return layers.reverse().reduce((under, top) => over(top, under), [255, 255, 255, 1]);
  };
  const problems = [];
  let checked = 0;
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  while (walker.nextNode()) {
    const text = walker.currentNode.textContent.trim();
    const element = walker.currentNode.parentElement;
    if (!text || element.closest('script,style,option,.visually-hidden,:disabled,[aria-hidden=true]')) continue;
    if (!element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) continue;
    const style = getComputedStyle(element);
    const size = parseFloat(style.fontSize), bold = Number(style.fontWeight) >= 700;
    const needed = size >= 24 || (size >= 18.66 && bold) ? 3 : 4.5;
    const value = ratio(over(parse(style.color), backdrop(element)), backdrop(element));
    checked++;
    if (value < needed) problems.push(`${value.toFixed(2)} "${text.slice(0, 30)}" <${element.tagName.toLowerCase()} class="${element.className}">`);
  }
  document.querySelectorAll('input:not([type=hidden]):not([type=checkbox]),select,.theme-toggle').forEach(control => {
    if (!control.checkVisibility()) return;
    const style = getComputedStyle(control);
    const value = ratio(parse(style.borderTopColor), backdrop(control.parentElement));
    if (control.matches('input,select') && value < 3) problems.push(`${value.toFixed(2)} control border <${control.tagName.toLowerCase()}>`);
  });
  const focus = parse(getComputedStyle(document.documentElement).getPropertyValue('--focus').trim().replace(/^#(..)(..)(..)$/,
      (_, r, g, b) => `rgb(${parseInt(r,16)},${parseInt(g,16)},${parseInt(b,16)})`));
  const focusRatio = ratio(focus, backdrop(document.querySelector('.panel, .workflow-grid article') || document.body));
  if (focusRatio < 3) problems.push(`${focusRatio.toFixed(2)} focus ring`);
  return { checked, problems };
}
"""

# Every status style and control, including states the sample flow doesn't reach.
FIXTURE_JS = """
() => {
  const box = document.createElement('div');
  box.id = 'theme-fixture';
  box.className = 'panel';
  box.innerHTML = `
    <span class="badge">New</span><span class="badge warn">Conflict</span><span class="badge duplicate">Duplicate</span>
    <span class="badge bad">Rejected</span><span class="badge good">Approved</span>
    <div class="notice error">Error</div><div class="notice success">Success</div><div class="notice info">Info</div>
    <p class="validation-message">Invalid</p><p class="guest-entry-error">Guest error</p>
    <div class="row-card"><ul class="issues"><li>Issue</li></ul></div>
    <button>Primary</button><button class="secondary">Secondary</button><button class="danger">Danger</button>
    <a href="#x">Link</a><p class="muted">Secondary text</p><span class="session-tag">Tag</span>
    <input value="Value"><select><option>Option</option></select>
    <div class="empty-state"><strong>Empty</strong><p>Help</p></div>
    <label class="upload-dropzone"><span class="upload-symbol">↑</span><span class="muted">Drop</span></label>
    <table><thead><tr><th>Head</th></tr></thead><tbody><tr><td>Cell</td></tr></tbody></table>`;
  document.body.appendChild(box);
}
"""

STATE_COLORS_JS = """
() => Object.fromEntries(['warn', 'duplicate', 'bad', 'good'].map(name => {
  const s = getComputedStyle(document.querySelector(`#theme-fixture .badge.${name}`));
  return [name, [s.color, s.backgroundColor]];
}))
"""


def lab(text):
    """CIE Lab, where a distance of about 2 is just noticeable and 15+ is an obvious difference."""
    channels = [float(v) / 255 for v in text.replace("rgb(", "").replace(")", "").split(",")[:3]]
    r, g, b = [v / 12.92 if v <= .04045 else ((v + .055) / 1.055) ** 2.4 for v in channels]
    xyz = (.4124 * r + .3576 * g + .1805 * b) / .9505, .2126 * r + .7152 * g + .0722 * b, (.0193 * r + .1192 * g + .9505 * b) / 1.089
    fx, fy, fz = [v ** (1 / 3) if v > .008856 else 7.787 * v + 16 / 116 for v in xyz]
    return 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)


def color_distance(a, b):
    return sum((x - y) ** 2 for x, y in zip(lab(a), lab(b))) ** .5


def audit(page, label):
    """Contrast of all visible text and controls on this page, in both themes."""
    start = theme(page)
    for wanted in ("light", "dark"):
        set_theme(page, wanted)
        page.wait_for_timeout(100)
        result = page.evaluate(AUDIT_JS)
        check(f"contrast {label} {wanted}", not result["problems"],
              f"{result['checked']} text nodes" + ("; " + "; ".join(result["problems"][:8]) if result["problems"] else ""))
    set_theme(page, start)


def audit_states(page):
    page.evaluate(FIXTURE_JS)
    audit(page, "status fixture")
    for wanted in ("light", "dark"):
        set_theme(page, wanted)
        colors = page.evaluate(STATE_COLORS_JS)
        names = list(colors)
        closest = min((max(color_distance(colors[a][0], colors[b][0]), color_distance(colors[a][1], colors[b][1])), f"{a}/{b}")
                      for i, a in enumerate(names) for b in names[i + 1:])
        check(f"conflict/duplicate/error/success are distinct {wanted}", closest[0] >= 15, f"closest pair {closest[1]}: {closest[0]:.0f} delta E")
    # The error bar and reconnect dialog only appear when something goes wrong.
    set_theme(page, "dark")
    result = page.evaluate("""() => {
      const bar = document.getElementById('blazor-error-ui');
      const dialog = document.getElementById('components-reconnect-modal');
      return bar && dialog ? [getComputedStyle(bar).color, getComputedStyle(dialog).color, getComputedStyle(dialog).backgroundColor] : null;
    }""")
    if result:
        check("error bar and reconnect dialog are readable in dark", result == ["rgb(20, 43, 39)", "rgb(228, 238, 232)", "rgb(24, 36, 31)"], str(result))
    page.evaluate("document.getElementById('theme-fixture').remove()")


def check_first_visit(browser):
    for scheme in ("light", "dark"):
        page = new_page(browser, scheme)
        page.goto(BASE_URL + "/")
        check(f"first visit follows system {scheme}", theme_is_applied(page, scheme) and saved(page) is None)
        page.context.close()

    page = new_page(browser, "light")
    page.goto(BASE_URL + "/")
    page.emulate_media(color_scheme="dark")
    page.wait_for_function("document.documentElement.getAttribute('data-theme') === 'dark'", timeout=5000)
    check("no saved choice keeps following system changes", theme_is_applied(page, "dark"))
    toggle(page).click()
    check("toggle saves the choice in localStorage", saved(page) == "light" and theme_is_applied(page, "light"))
    page.reload()
    check("saved choice beats system preference after refresh", theme_is_applied(page, "light"))
    page.context.close()

    page = new_page(browser, "light")
    page.add_init_script(f"localStorage.setItem('{STORAGE_KEY}', 'banana')")
    page.goto(BASE_URL + "/")
    check("an invalid saved value falls back to the system theme", theme_is_applied(page, "light"))
    page.context.close()


def brightest_frame_during_refresh(page, urls):
    """Records every painted frame while refreshing a dark page on a slowed-down browser."""
    page.goto(BASE_URL + "/")
    page.evaluate(f"localStorage.setItem('{STORAGE_KEY}', 'dark')")
    page.reload()
    page.wait_for_timeout(500)  # Don't record a frame left over from before the refresh.
    cdp = page.context.new_cdp_session(page)
    cdp.send("Emulation.setCPUThrottlingRate", {"rate": 4})
    frames = []

    def on_frame(event):
        frames.append(event["data"])
        cdp.send("Page.screencastFrameAck", {"sessionId": event["sessionId"]})

    cdp.on("Page.screencastFrame", on_frame)
    cdp.send("Page.startScreencast", {"format": "png", "everyNthFrame": 1})
    for url in urls:
        page.goto(BASE_URL + url)
        page.reload()
        page.wait_for_timeout(700)
    cdp.send("Page.stopScreencast")
    brightest = 0
    for frame in frames:
        with Image.open(io.BytesIO(base64.b64decode(frame))) as image:
            # Bottom corners are the page or section background on these public pages.
            gray = image.convert("L")
            width, height = gray.size
            brightest = max(brightest, gray.getpixel((2, height - 3)), gray.getpixel((width - 3, height - 3)))
    return len(frames), brightest


def check_no_flash(browser):
    """Dark must be set before <body> exists, and no painted frame may be light."""
    page = new_page(browser, "light")
    page.add_init_script("""
      new MutationObserver((_, observer) => {
        if (document.body) { window.themeAtBody = document.documentElement.getAttribute('data-theme'); observer.disconnect(); }
      }).observe(document, { childList: true, subtree: true });
    """)
    count, brightest = brightest_frame_during_refresh(page, ["/", "/login"])
    check("theme is set before <body> is parsed", page.evaluate("window.themeAtBody") == "dark")
    check("no light frame during dark refresh (screencast, 4x CPU throttle)", count > 1 and brightest < 60, f"{count} frames, brightest corner {brightest}/255")
    page.context.close()

    # Control: without theme.js the same measurement has to see light frames.
    control = browser.new_context(color_scheme="light", viewport=DESKTOP).new_page()
    control.route("**/theme*.js", lambda route: route.abort())
    count, brightest = brightest_frame_during_refresh(control, ["/", "/login"])
    check("control: the frame check detects light when theme.js is blocked", brightest > 200, f"{count} frames, brightest corner {brightest}/255")
    control.context.close()


def check_keyboard(page, label, tabs):
    """Tabs from the start of the page, as Blazor moves focus to the page heading on navigation."""
    start = theme(page)
    other = "dark" if start == "light" else "light"
    page.locator(".skip-link").focus()
    for _ in range(tabs):
        page.keyboard.press("Tab")
        if page.evaluate("document.activeElement?.hasAttribute('data-theme-toggle') ?? false"):
            break
    focused = page.evaluate("""() => { const e = document.activeElement; const s = getComputedStyle(e);
        return { toggle: e.hasAttribute('data-theme-toggle'), ring: e.matches(':focus-visible') && s.outlineStyle === 'solid' && parseFloat(s.outlineWidth) >= 3 }; }""")
    check(f"{label}: Tab reaches the toggle with a visible focus ring", focused["toggle"] and focused["ring"])
    if not focused["toggle"]:
        return  # Enter would activate whatever else has focus.
    page.keyboard.press("Enter")
    after_enter = theme(page)
    still_focused = page.evaluate("document.activeElement?.hasAttribute('data-theme-toggle') ?? false")
    page.keyboard.press("Space")
    check(f"{label}: Enter and Space switch the theme and keep focus", after_enter == other and theme(page) == start and still_focused)


def check_public_navigation(browser):
    page = new_page(browser, "light")
    page.goto(BASE_URL + "/")
    check_keyboard(page, "landing", 8)
    toggle(page).click()
    page.evaluate("window.sameDocument = true")
    page.get_by_role("link", name="Sign in", exact=True).click()
    page.get_by_role("heading", name="Sign in").wait_for()
    enhanced = page.evaluate("window.sameDocument === true")
    check("theme survives enhanced navigation to sign-in", theme_is_applied(page, "dark"), f"same document: {enhanced}")
    audit(page, "sign-in")
    capture(page, "sign-in")
    page.context.close()


def run_guest_flow(browser):
    page = new_page(browser, "light")
    page.goto(BASE_URL + "/")
    capture(page, "landing")
    audit(page, "landing")
    audit_states(page)

    set_theme(page, "dark")
    page.get_by_role("button", name="Try as guest").click()
    page.wait_for_url("**/workspace")
    page.get_by_text("No imports yet").wait_for()
    check("dark choice survives Try as guest", theme_is_applied(page, "dark"))
    check_keyboard(page, "workspace", 8)
    capture(page, "guest-dashboard")
    audit(page, "guest dashboard")
    audit_states(page)

    page.locator(".app-sidebar").get_by_role("link", name="Customers").click()
    page.get_by_role("cell", name="Avery Patel").first.wait_for()
    page.locator(".app-sidebar").get_by_role("link", name="Imports").click()
    page.get_by_role("heading", name="Imports", exact=True).wait_for()
    audit(page, "imports (empty)")
    set_theme(page, "dark")
    page.reload()
    page.get_by_role("heading", name="Imports", exact=True).wait_for()
    check("theme survives workspace navigation and refresh", theme_is_applied(page, "dark"))
    page.locator(".app-sidebar").get_by_role("link", name="Overview").click()

    page.get_by_role("button", name="Import sample file").click()
    page.wait_for_url("**/imports/*")
    save_mapping = page.get_by_role("button", name="Save mapping")
    save_mapping.wait_for(timeout=45_000)
    check("sample file opens with its 4 rows", page.get_by_text("4 row(s)").is_visible())
    check("mapping is suggested from the headers", page.get_by_label("Name (required)").input_value() != "")
    audit(page, "import mapping")
    save_mapping.click()
    page.get_by_role("button", name="Validate batch").click()
    rows = page.locator("[data-source-row]")
    rows.nth(0).get_by_text("Conflict").wait_for(timeout=45_000)
    results = [rows.nth(i).locator(".row-card-title").inner_text().replace("\n", " ") for i in range(4)]
    check("validation finds conflict, new, duplicate, and an invalid row",
          "Conflict" in results[0] and "New" in results[1] and "Duplicate" in results[2] and "issue" in results[3], " | ".join(results))

    rows.nth(0).get_by_role("checkbox").check()
    rows.nth(0).get_by_role("button", name="Approve update").click()
    rows.nth(0).locator(".badge.good").wait_for()
    rows.nth(2).get_by_label("Rejection reason").fill("Already in the directory")
    rows.nth(2).get_by_role("button", name="Reject").click()
    rows.nth(2).locator(".badge.bad", has_text="Rejected").wait_for()
    check("row badges use separate conflict, duplicate, issue, approved, and rejected styles",
          all(rows.nth(i).locator(selector).first.is_visible() for i, selector in
              ((0, ".badge.warn"), (0, ".badge.good"), (2, ".badge.duplicate"), (2, ".badge.bad"), (3, ".badge.bad"))))
    audit(page, "import review")
    audit_states(page)
    rows_top = page.get_by_role("heading", name="Rows", exact=True).evaluate("e => Math.round(e.getBoundingClientRect().top + scrollY) - 16")
    capture(page, "import-review", scroll_y=rows_top)
    capture(page, "import-review-full", full_page=True)

    rows.nth(1).get_by_role("button", name="Approve insert").click()
    rows.nth(1).locator(".badge.good").wait_for()
    rows.nth(3).get_by_role("button", name="Correct row").click()
    email = rows.nth(3).get_by_label("Email")
    email.fill("taylor@example.test")
    email.press("Tab")
    rows.nth(3).get_by_role("button", name="Save correction").click()
    rows.nth(3).get_by_role("button", name="Approve insert").click()
    rows.nth(3).locator(".badge.good").wait_for()
    check("row review: corrected row loses its issue and is approved",
          rows.nth(3).get_by_text("Corrected").is_visible() and rows.nth(3).locator(".badge.bad").count() == 0)

    set_theme(page, "dark")
    page.get_by_role("button", name="Commit batch").click()
    summary = page.get_by_test_id("commit-summary")
    summary.wait_for(timeout=45_000)
    text = summary.inner_text().split()
    check("commit: 2 inserted, 1 updated, 1 rejected", text == ["Inserted", "2", "Updated", "1", "Rejected", "1"], " ".join(text))
    check("theme survives commit", theme_is_applied(page, "dark"))
    audit(page, "committed import")

    page.locator(".app-sidebar").get_by_role("link", name="Customers").click()
    page.get_by_text("5 customer(s)").wait_for()
    names = page.locator(".record-name").all_inner_texts()
    check("committed rows are in Customers", {"Casey Morgan", "Taylor Quinn", "Avery Patel"} <= set(names), ", ".join(names))
    check("theme survives navigation to Customers", theme_is_applied(page, "dark"))
    capture(page, "customers")
    audit(page, "customers")
    page.reload()
    page.get_by_text("5 customer(s)").wait_for()
    check("saved choice survives Customers refresh (last set: dark)", theme_is_applied(page, "dark"))

    page.locator(".app-sidebar").get_by_role("link", name="Imports").click()
    page.get_by_role("link", name="sample-customers.csv").wait_for()
    audit(page, "imports list")
    page.get_by_role("button", name="Exit guest").click()
    page.wait_for_url(BASE_URL + "/**")
    check("theme survives Exit guest", theme_is_applied(page, "dark"))
    page.context.close()


def no_overflow(page):
    return page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")


def check_mobile(browser):
    page = new_page(browser, "light", viewport=MOBILE, device_scale_factor=2, is_mobile=True, has_touch=True)
    page.goto(BASE_URL + "/")
    toggle(page).tap()
    check("mobile: tap switches the landing page to dark", theme_is_applied(page, "dark") and saved(page) == "dark")
    page.reload()
    check("mobile: dark survives refresh", theme_is_applied(page, "dark"))
    check("mobile: landing has no sideways scroll", no_overflow(page))

    page.get_by_role("button", name="Try as guest").tap()
    page.wait_for_url("**/workspace")
    page.get_by_text("No imports yet").wait_for()
    check("mobile: dark survives Try as guest", theme_is_applied(page, "dark"))
    box = toggle(page).bounding_box()
    check("mobile: toggle is on screen and at least 42px", box["x"] >= 0 and box["x"] + box["width"] <= MOBILE["width"] and min(box["width"], box["height"]) >= 42, str(box))
    capture(page, "guest-dashboard-mobile", tap=True)
    with Image.open(SHOTS / "guest-dashboard-mobile-light.png") as light, Image.open(SHOTS / "guest-dashboard-mobile-dark.png") as dark:
        check("mobile: light and dark layouts line up", light.size == dark.size)
    tops = []
    for wanted in ("light", "dark"):
        set_theme(page, wanted, tap=True)
        tops.append(page.locator("#main-content h1").evaluate("e => e.getBoundingClientRect().top + scrollY"))
    check("mobile: heading is at the same height in both themes", tops[0] == tops[1], str(tops))
    set_theme(page, "dark", tap=True)
    set_theme(page, "light", tap=True)
    set_theme(page, "dark", tap=True)
    look = page.evaluate("""() => {
      const button = getComputedStyle(document.querySelector('[data-theme-toggle]'));
      const paper = getComputedStyle(document.documentElement).getPropertyValue('--paper').trim();
      const probe = document.createElement('i');
      probe.style.color = paper;
      document.body.append(probe);
      const expected = getComputedStyle(probe).color;
      probe.remove();
      return { pressed: button.backgroundColor !== expected, sidebarEdge: getComputedStyle(document.querySelector('.app-sidebar')).borderRightWidth };
    }""")
    check("mobile: toggle doesn't stay highlighted after a tap", not look["pressed"])
    check("mobile: no stray sidebar edge in dark", look["sidebarEdge"] == "0px")
    audit(page, "mobile dashboard")

    page.locator(".app-sidebar").get_by_role("link", name="Customers").tap()
    page.get_by_role("cell", name="Avery Patel").first.wait_for()
    check("mobile: dark survives navigation", theme_is_applied(page, "dark"))
    page.reload()
    page.get_by_role("cell", name="Avery Patel").first.wait_for()
    check("mobile: dark survives workspace refresh", theme_is_applied(page, "dark"))
    toggle(page).tap()
    check("mobile: tap switches the workspace back to light", theme_is_applied(page, "light") and saved(page) == "light")
    audit(page, "mobile customers")

    for width in (320, 360, 430, 768):
        page.set_viewport_size({"width": width, "height": 800})
        page.wait_for_timeout(100)
        box = toggle(page).bounding_box()
        check(f"mobile: toggle fits at {width}px with no sideways scroll", no_overflow(page) and box["x"] + box["width"] <= width)
    page.context.close()


def main():
    SHOTS.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()
        print(f"Chromium {browser.version}, Playwright against {BASE_URL}")
        check_first_visit(browser)
        check_no_flash(browser)
        check_public_navigation(browser)
        run_guest_flow(browser)
        check_mobile(browser)
        browser.close()
    check("no browser errors or failed requests", not browser_errors, "; ".join(browser_errors[:6]))
    print(f"\n{'FAILED: ' + str(len(failures)) if failures else 'ALL PASSED'}")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
