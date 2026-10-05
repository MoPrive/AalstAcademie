// Sprint 004: de managerselect bij beide interne types blijft optioneel; alleen data-type-required maakt velden verplicht.
/* Browserondersteuning; servervalidatie en autorisatie blijven de beslissende controles. */
(() => {
    'use strict';
    // Alleen registratie heeft typegroepen; hetzelfde script ondersteunt ook de foutfocus bij login en review.
    const form = document.querySelector('[data-registration-form]');
    if (form) {
        const type = form.querySelector('[name="Input.RequestedAccountType"]');
        // De gekozen numerieke accounttypewaarde wordt vergeleken met de expliciete types per veldgroep.
        function updateGroups() {
            form.querySelectorAll('[data-registration-types]').forEach(group => {
                const active = group.dataset.registrationTypes.split(' ').includes(type.value);
                group.hidden = !active;
                group.querySelectorAll('input, select, textarea').forEach(control => {
                    // Disabled controls worden niet gepost; alleen verbergen zou onvoldoende zijn.
                    control.disabled = !active;
                    // Optionele velden blijven optioneel; verplichte typevelden gelden uitsluitend binnen de actieve groep.
                    control.required = active && control.hasAttribute('data-type-required');
                });
            });
        }
        // Initialisatie is ook nodig na servervalidatie, wanneer de gemaakte typekeuze opnieuw wordt weergegeven.
        type.addEventListener('change', updateGroups); updateGroups();
    }
    // Na een geweigerde POST ziet en hoort een toetsenbordgebruiker eerst de serverfouten, zonder veldinvoer te wissen.
    const summary = document.querySelector('.validation-summary-errors');
    if (summary) { summary.setAttribute('tabindex', '-1'); summary.focus(); }
})();
