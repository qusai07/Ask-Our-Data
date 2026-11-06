// Small DOM helpers for the chat page. No data processing happens here.
window.aod = {
  initComposer(id, dotnetRef) {
    const el = document.getElementById(id);
    if (!el || el.dataset.aodInit) return;
    el.dataset.aodInit = "1";
    const grow = () => {
      el.style.height = "auto";
      el.style.height = Math.min(el.scrollHeight, 180) + "px";
    };
    el.addEventListener("input", grow);
    el.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        const text = el.value.trim();
        if (text.length > 0 && !el.disabled) {
          dotnetRef.invokeMethodAsync("SubmitFromKeyboard", text);
          el.value = "";
          grow();
        }
      }
    });
    grow();
    el.focus();
  },

  focus(id) {
    const el = document.getElementById(id);
    if (el && !el.disabled) {
      el.style.height = "auto";
      el.focus();
    }
  },

  scrollToEnd(id) {
    const el = document.getElementById(id);
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "smooth" });
  },

  async copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      const ta = document.createElement("textarea");
      ta.value = text;
      document.body.appendChild(ta);
      ta.select();
      document.execCommand("copy");
      ta.remove();
    }
  },

  downloadText(filename, text, mime) {
    const blob = new Blob(["﻿" + text], { type: mime });   // BOM so Excel reads Arabic correctly
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  },
};
