document.addEventListener("submit", async event => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-guest-entry")) {
        return;
    }

    event.preventDefault();
    const button = form.querySelector("button");
    const message = document.querySelector("[data-guest-error]");
    button.disabled = true;
    message.hidden = true;

    try {
        const response = await fetch("/account/guest-token", {
            method: "POST",
            credentials: "same-origin",
            body: new URLSearchParams(new FormData(form))
        });
        if (!response.ok) {
            throw new Error(response.status === 429
                ? "Too many guest requests. Wait a few minutes and try again."
                : "The guest workspace could not start. Reload this page and try again.");
        }

        const entry = await response.json();
        form.elements.namedItem("token").value = entry.token;
        form.submit();
    } catch (error) {
        message.textContent = error.message;
        message.hidden = false;
        button.disabled = false;
    }
});
