/* SignalR meldt alleen dat iets veranderde. Elke teller/lijst komt uit een beveiligde snapshot. */
(() => {
    'use strict';
    // Alleen pagina's met een bevoegde beheerteller starten deze ondersteuning.
    const badges = [...document.querySelectorAll('[data-account-pending-count]')];
    if (!badges.length) return;
    const queue = document.querySelector('[data-account-applications]');
    const rows = document.querySelector('[data-account-application-rows]');
    const pages = document.querySelector('[data-account-application-pages]');
    const caption = document.querySelector('[data-account-application-caption]');
    const statusNames = { Pending: 'In afwachting', Refused: 'Geweigerd', Approved: 'Goedgekeurd' };
    const statuses = [...document.querySelectorAll('[data-account-applications-status]')];
    // Op detailpagina's bestaat alleen de teller; op het overzicht volgen status en pagina de serverweergave.
    let selectedStatus = queue?.dataset.status || 'Pending';
    let selectedPage = Number(queue?.dataset.page || 1);
    // generation bewaakt de selectie, busy serialiseert fetches, dirty bundelt meldingen en stopped sluit toegang af.
    let generation = 0, busy = false, dirty = false, stopped = false;
    let connection = null;
    // Begrensde verbindingpogingen; deze wachttijden vormen geen timer die voortdurend bedrijfsgegevens opvraagt.
    const delays = [0, 2000, 10000, 30000];

    // aria-live verandert alleen bij een betekenisvolle wijziging, niet bij iedere geslaagde fetch.
    function setStatus(message) {
        statuses.forEach(node => { if (node.textContent !== message) node.textContent = message; });
    }
    // Bij 401/403 stopt verversing, maar open formulierinvoer blijft op het scherm staan.
    function stopForAccess() {
        stopped = true;
        connection?.stop();
        setStatus('Je toegang is verlopen. Meld je opnieuw aan.');
    }
    // Oude accounts zonder aanvraagdatum krijgen een eerlijke onbekend-aanduiding in plaats van een verzonnen tijdstip.
    function requestedDate(value) {
        if (!value) return 'Onbekend';
        // SQLite bewaart deze DateTime als UTC, ook wanneer de serializer geen zone toevoegt.
        const date = new Date(/Z$|[+-]\d\d:\d\d$/.test(value) ? value : value + 'Z');
        return Number.isNaN(date.getTime()) ? 'Onbekend' : new Intl.DateTimeFormat('nl-BE',
            { dateStyle: 'short', timeStyle: 'short' }).format(date);
    }
    // Alleen aangewezen leesgebieden wijzigen: teller, tabel en paginering; een reviewformulier wordt nooit vervangen.
    function render(snapshot) {
        badges.forEach(node => {
            const count = String(snapshot.totalPending);
            if (node.textContent !== count) node.textContent = count;
        });
        // Een detailformulier heeft geen queue-tbody. Zijn inputs en oorspronkelijke versie blijven intact.
        if (!rows || !queue) return;
        // Ook de naam voor schermlezers volgt de werkelijk geladen tab/snapshot.
        if (caption) caption.textContent = `${statusNames[snapshot.status] || snapshot.status}: accountaanvragen`;
        const fragment = document.createDocumentFragment();
        const typeNames = { Employee: 'Medewerker', InternalInstructor: 'Interne lesgever', ExternalInstructor: 'Externe lesgever' };
        snapshot.rows.forEach(row => {
            const tr = document.createElement('tr');
            const nameCell = document.createElement('td');
            // textContent behandelt namen als tekst; encodeURIComponent houdt een account-ID binnen het routeonderdeel.
            const link = document.createElement('a');
            link.href = '/Admin/AccountApplications/' + encodeURIComponent(row.id);
            link.textContent = row.displayName;
            nameCell.append(link); tr.append(nameCell);
            [typeNames[row.requestedAccountType] || 'Onbekend', row.departmentOrOrganization || '—',
                requestedDate(row.accountRequestedAtUtc), statusNames[row.status] || row.status].forEach(value => {
                const td = document.createElement('td'); td.textContent = value; tr.append(td);
            });
            fragment.append(tr);
        });
        if (!snapshot.rows.length) {
            const tr = document.createElement('tr'), td = document.createElement('td');
            td.colSpan = 5; td.textContent = 'Geen aanvragen in deze lijst.'; tr.append(td); fragment.append(tr);
        }
        // Het fragment bouwt alle rijen buiten de zichtbare tabel op en vervangt vervolgens de lijst in één stap.
        rows.replaceChildren(fragment);
        if (pages) {
            const pageLinks = document.createDocumentFragment();
            // Twee gewone links houden paginering ook zonder JavaScript bruikbaar en begrensd.
            for (const [label, page] of [['Vorige', snapshot.page - 1], ['Volgende', snapshot.page + 1]]) {
                if (page < 1 || page > snapshot.totalPages) continue;
                const a = document.createElement('a'); a.textContent = label;
                a.className = 'btn btn-outline-secondary me-2';
                a.href = '/Admin/AccountApplications?status=' + encodeURIComponent(selectedStatus) + '&page=' + page;
                a.dataset.accountPage = String(page); pageLinks.append(a);
            }
            const summary = document.createElement('span');
            summary.textContent = `Pagina ${snapshot.page} van ${snapshot.totalPages}`;
            pageLinks.append(summary); pages.replaceChildren(pageLinks);
        }
    }

    // Eén fetch tegelijk. Een burst tijdens het ophalen vraagt precies één trailing fetch aan.
    async function refresh() {
        // Ook een verborgen tab onthoudt dat gegevens kunnen ontbreken; bij terugkeer wordt opnieuw gelezen.
        dirty = true;
        if (busy || stopped || document.hidden) return;
        busy = true;
        try {
            do {
                dirty = false;
                const requestGeneration = generation;
                const url = '/Admin/AccountApplications/Snapshot?status=' + encodeURIComponent(selectedStatus) + '&page=' + selectedPage;
                try {
                    // De eigen Identity-cookie verifieert de caller; een verse servercontrole vervangt browsercache.
                    const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store',
                        headers: { Accept: 'application/json', 'X-Requested-With': 'XMLHttpRequest' } });
                    if (response.status === 401 || response.status === 403) { stopForAccess(); break; }
                    if (!response.ok) throw new Error('Snapshot unavailable');
                    const snapshot = await response.json();
                    // Een oud pagina/filterantwoord overschrijft nooit een nieuwere selectie.
                    if (requestGeneration === generation && !document.hidden) {
                        render(snapshot);
                        setStatus(connection?.state === 'Connected' ? 'Live verbonden.' : 'Gegevens bijgewerkt; live verbinding niet actief.');
                    } else dirty = true;
                } catch {
                    setStatus('Verversen is niet gelukt. Probeer handmatig opnieuw.');
                    break; // Geen onbegrensde herhaling bij een onbereikbare server.
                }
            } while (dirty && !stopped && !document.hidden);
        } finally { busy = false; }
    }
    // De herstelknop werkt op overzicht én detail; een beëindigde verbinding krijgt opnieuw begrensde startpogingen.
    document.querySelectorAll('[data-account-applications-refresh]').forEach(button =>
        button.addEventListener('click', () => { refresh(); if (!connection || connection.state === 'Disconnected') start(); }));
    // Eventdelegatie blijft werken wanneer render de vorige/volgende links heeft vervangen.
    pages?.addEventListener('click', event => {
        const link = event.target.closest('a[data-account-page]');
        if (!link) return;
        event.preventDefault(); selectedPage = Number(link.dataset.accountPage); generation++;
        history.replaceState(null, '', link.href); refresh();
    });
    // Een ander statusfilter begint op pagina één en maakt eventuele eerdere antwoorden ongeldig.
    queue?.querySelectorAll('[data-account-status]').forEach(link => link.addEventListener('click', event => {
        event.preventDefault(); selectedStatus = link.dataset.accountStatus; selectedPage = 1; generation++;
        queue.dataset.status = selectedStatus; queue.dataset.page = '1';
        queue.querySelectorAll('[data-account-status]').forEach(tab => {
            tab.classList.toggle('active', tab === link);
            if (tab === link) tab.setAttribute('aria-current', 'page'); else tab.removeAttribute('aria-current');
        });
        history.replaceState(null, '', link.href); refresh();
    }));
    // Terugkeer naar een tab of venster herstelt mogelijk gemiste meldingen met een volledige snapshot.
    document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
    window.addEventListener('focus', refresh);

    // starting voorkomt dat gelijktijdige handmatige herstelacties meer dan één connectiereeks starten.
    let starting = false;
    async function start() {
        if (starting || stopped || !connection || connection.state !== 'Disconnected') return;
        starting = true;
        try {
            for (const delay of delays) {
                if (stopped) break;
                if (delay) await new Promise(resolve => setTimeout(resolve, delay));
                if (stopped) break;
                // Een geslaagde start leest opnieuw: wijzigingen tussen eerste snapshot en verbinding zijn dan meegenomen.
                try { await connection.start(); setStatus('Live verbonden.'); refresh(); return; }
                catch (error) {
                    if (error?.statusCode === 401 || error?.statusCode === 403) { stopForAccess(); return; }
                }
            }
            if (!stopped) setStatus('Live verbinding niet beschikbaar. Ververs handmatig of verbind opnieuw.');
        } finally { starting = false; }
    }
    refresh(); // Eerste snapshot; nogmaals na connect om de opstartrace te sluiten.
    if (!window.signalR) { setStatus('Live verbinding niet beschikbaar. Je kunt handmatig verversen.'); return; }
    // SignalR draagt alleen een leeg wijzigingsevenement; alle gegevens blijven achter de snapshotautorisatie.
    connection = new signalR.HubConnectionBuilder().withUrl('/hubs/account-applications')
        .withAutomaticReconnect(delays).configureLogging(signalR.LogLevel.None).build();
    connection.on('AccountApplicationsChanged', refresh); // Handler vóór connect registreren.
    connection.onreconnecting(() => setStatus('Verbinding onderbroken. Opnieuw verbinden…'));
    // Tijdens een onderbreking kunnen meldingen ontbreken; herstel vertrouwt daarom op de huidige databaseversie.
    connection.onreconnected(() => { setStatus('Live verbonden.'); refresh(); });
    connection.onclose(() => {
        if (!stopped) setStatus('Live verbinding gestopt. Ververs handmatig of verbind opnieuw.');
    });
    start();
})();
