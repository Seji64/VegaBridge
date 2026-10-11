window.addressAutocomplete = {
    // "Fill in" arrow of a suggestion: writes the text into the input as if it
    // was typed. MudInput listens to "input" (Immediate), so MudAutocomplete
    // runs its normal debounce → search path and the dropdown stays open.
    fillIn: function (inputId, text, caret) {
        const input = document.getElementById(inputId);
        if (!input) return;
        input.focus();
        input.value = text;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.setSelectionRange(caret, caret);
    }
};
