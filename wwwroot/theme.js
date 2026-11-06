// Loaded as a blocking script in <head>, so the theme is set before the first paint.
(function () {
    const storageKey = "databridge-theme";
    const root = document.documentElement;
    const systemTheme = window.matchMedia("(prefers-color-scheme: dark)");

    function readSavedTheme() {
        try {
            const saved = localStorage.getItem(storageKey);
            return saved === "light" || saved === "dark" ? saved : null;
        } catch {
            return null; // Storage can be blocked. The choice then lasts for this page only.
        }
    }

    let savedTheme = readSavedTheme();

    function applyTheme() {
        const theme = savedTheme ?? (systemTheme.matches ? "dark" : "light");
        if (root.getAttribute("data-theme") !== theme) {
            root.setAttribute("data-theme", theme);
        }
    }

    applyTheme();

    // Until the visitor chooses, keep following the system setting.
    systemTheme.addEventListener("change", applyTheme);

    // Blazor's enhanced navigation copies <html> attributes from the server's response,
    // which has no data-theme. This puts it back before the browser paints.
    new MutationObserver(applyTheme).observe(root, { attributes: true, attributeFilter: ["data-theme"] });

    // Keep other open tabs in step.
    window.addEventListener("storage", event => {
        if (event.key === storageKey || event.key === null) {
            savedTheme = readSavedTheme();
            applyTheme();
        }
    });

    // The toggle can be added or replaced by Blazor at any time, so listen on the document.
    document.addEventListener("click", event => {
        if (!(event.target instanceof Element) || !event.target.closest("[data-theme-toggle]")) {
            return;
        }

        savedTheme = root.getAttribute("data-theme") === "dark" ? "light" : "dark";
        try {
            localStorage.setItem(storageKey, savedTheme);
        } catch {
            // Already handled when reading: the theme still changes for this page.
        }
        applyTheme();
    });
})();
