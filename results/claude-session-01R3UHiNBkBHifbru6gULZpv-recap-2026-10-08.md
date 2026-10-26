# Mempool DoS campaign: recap e ripresa

- Sessione: `session_01R3UHiNBkBHifbru6gULZpv`, Claude “Mempool DoS Benchmark”.
- Transcript locale: `~/.claude-nethermind/projects/-home-manuel-Work-Nethermind-Projects-nethermind/e7c685ec-afc4-4c3f-ad51-f2fa243c24b0.jsonl`.
- Verifica: 2026-10-08. Numeri ricontrollati nei report v5 e nelle righe fair-share.
- Stato: campagna v5 completata; mitigazioni successive ancora in review. Bozze sotto, non inviate a Daniil.

## Evoluzione della sessione

| Fase | Risultato |
|---|---|
| Review iniziale, 27 settembre | Problemi metodologici: vittima troppo breve, effetto scheduler, definizioni concorrenti di `R_max`, deliverable ancora senza dati v2 |
| v2, 28–29 settembre | Profilo soispoke aggiornato, preregistrazione e risultati; Notion e artifact condivisi |
| v3, 29–30 settembre | Domande di Stefano: passaggio da tx/s al lavoro in gas/s, separazione dei percorsi import/build |
| v4, 1–3 ottobre | Suite corte per domanda; tre macchine; quota CPU `f` e modello `Δ_build(T) = T·f/(1−f)` |
| v5, 5–6 ottobre | Rerun con le protezioni di master; correzione delle ipotesi del harness dopo la cache delle firme; builder a 3,8 GHz |
| Estensione, 7 ottobre | Budget gas per peer, poi quota equa di tempo per head; misure separate dalla campagna v5 |
| Ultima richiesta | Aprire il fix delle firme come PR ready, leggere Claude review, poi valutare aggiornamenti a Daniil e deliverable |

## Risultati da mantenere

- A parità di gas di spam/s, danno simile ai diversi cap. Il cap moltiplica il lavoro offerto da un peer.
- Candidato: **300k**, intervallo proposto **236k–300k**. Build valutato sul tier local builder; full node riportato come perdita di tx oneste.
- Il cap non è una garanzia universale: dipende da hardware, rate sostenuto e soglia `B`.
- v5, keccak-wide, 300k e 30M gas/s: tx oneste ammesse **27,6% Builder / 25,4% Mid / 12,2% Low**.
- Low è `n1-standard-8`, un proxy. Il tier full node da ~1000 single-thread resta un'estrapolazione.
- Builder v5: 3,8 GHz. Builder peer-budget/fair-share: circa 5,1 GHz. Confronti interni validi; tempi assoluti non intercambiabili.
- Import protetto molto meno colpito del percorso diretto. Alcune righe sig-stuffed su Low non sostengono il rate richiesto.
- Fonte v5: `~/frame-tx-campaign-keep/analysis/runs-v5/analysis/SUMMARY.md`.

## PR e ripresa effettuata

| PR | Stato verificato | Scopo |
|---|---|---|
| [#14135](https://github.com/NethermindEth/nethermind/pull/14135) | Merged | Harness delle suite |
| [#14085](https://github.com/NethermindEth/nethermind/pull/14085) | Merged | Preemption della simulazione durante build |
| [#14265](https://github.com/NethermindEth/nethermind/pull/14265) | Open, ready, mergeable | Preemption tra verifiche delle firme durante import/build |
| [#14394](https://github.com/NethermindEth/nethermind/pull/14394) | Open, draft | Budget di gas per peer, off di default |
| [#14414](https://github.com/NethermindEth/nethermind/pull/14414) | Open, draft, stacked su #14394 | Quota di tempo per peer, off di default |
| [#14431](https://github.com/NethermindEth/nethermind/pull/14431) | Aperta ready nella ripresa | Rinvio prima delle firme quando il budget per head è esaurito |

### #14431

- Commit iniziale `7b172a17e4`, follow-up `55f9549338`; indipendente dallo stack delle guardie.
- Rinvio anticipato solo per gossip che richiede simulazione. Locali e payer risolti nativamente mantengono la verifica firme.
- CI sul commit iniziale: suite TxPool, Consensus e Blockchain riuscite, anche checked/no-intrinsics; lint e whitespace riusciti. La verifica locale del follow-up è riportata sotto.
- Claude, prima review: 0 Critical, 0 High, 1 Medium, 3 Low.
- Medium: il rinvio salta anche filtri plugin, deployed-code, prove KZG e limiti paymaster. Le tx rinviate non entrano nel pool; una retry deve attraversare tutta la validazione.
- Compromesso esplicitato nei Remarks e accettato nella seconda review.
- [flcl42](https://github.com/NethermindEth/nethermind/pull/14431#discussion_r4216653487): il contatore mancante rompe anche la contabilità del harness. Confermato nel codice e riprodotto dal test: atteso 1, trovato 0.
- Fix pubblicato in `55f9549338`: incremento di `FrameTxSimulationsBudgetExhausted` accanto al contatore dei rinvii. Nessun doppio conteggio: il simulatore non viene chiamato sul ramo anticipato.
- Test parametrizzato esteso, non parallelo: entrambi i contatori incrementano una volta sul rinvio; nessun incremento per locale o budget disponibile.
- Dopo il fix: build senza warning/errori; suite TxPool completa **1594 passati, 46 saltati, 0 falliti**; formato verificato.
- [Review finale Claude](https://github.com/NethermindEth/nethermind/pull/14431#issuecomment-6056236531): **mergeable**, nessun blocker; counter fix confermato. Status `claude-review/reviewed` riuscito su `55f9549338`.
- Low residui: nuova property dell'interfaccia, accettata; manca un caso di test dedicato al payer nativo con budget esaurito, opzionale.
- Nessun rerun v5 necessario per il fix del contatore: le misure storiche non includono #14431. Nessun nuovo benchmark dichiarato per #14431.
- EXPB fallito a monte: login Docker Hub `unauthorized: incorrect username or password`; report senza immagine/revisione. Richiede verifica delle credenziali CI.

## Quota equa: risultato esatto

Mediane di due run, un peer attaccante, un peer onesto, 1000 ms/head, head ogni 12 s, keccak-wide. Quote ammesse, %:

| Macchina e protezione | 300k | 400k | 500k |
|---|---:|---:|---:|
| Builder, off | 37,5 | 27,1 | 18,0 |
| Builder, gas | 37,7 | 29,0 | 29,2 |
| Builder, fair share | 100,0 | 100,0 | 89,8 |
| Low, off | 12,4* | 8,6 | 2,2 |
| Low, gas | 14,1* | 5,2* | 5,4 |
| Low, fair share | 99,5 | 97,2 | 97,0 |

- `*`: almeno una run non sostenuta. Nessun verdetto da quelle celle.
- Builder: miglioramento confermato rispetto al gas; criterio “flat” mancato di 0,2 punti a 500k, soglia ±10 punti rispetto a 300k.
- Low: fair share “flat”; confronto formale col gas non conclusivo per righe non sostenute.
- Tre attaccanti: direzione favorevole, ma nessun verdetto complessivo Sybil.
- “Una configurazione confermata per entrambe le macchine”: non stabilito dalle regole preregistrate.
- La singola run Builder a 500k scende a 86,7%. Non presentare “90–100%” come intervallo di tutte le run.
- Solo honest admission, solo keccak-wide. Nessuna nuova misura di build delay, nessuna misura fair-share sig-stuffed.
- #14431 non era incluso in queste run: non attribuirgli questi benefici.
- Fonte: `Fair share pre-registration.md`; script originale rieseguito su `analysis/runs-fs`.

## Bozza per Daniil

Measured your per-head fair-share idea in #14414: stock 1000 ms/head, 300k/400k/500k, two runs on Builder and n1, one attacker peer at 100 tx/s and one honest peer.

Honest admission, median of two runs:
- Builder: **100 / 100 / 89.8%**, versus **37.7 / 29.0 / 29.2%** with the 30M gas/s budget.
- n1: **99.5 / 97.2 / 97.0%**. Some gas-budget comparison rows were unsustained, so that comparison is descriptive.

The improvement is substantial. Builder at 500k narrowly misses our pre-registered flatness rule by 0.2 percentage points. The three-attacker case is directional only because some rows were unsustained. This experiment measures admission, not block-build delay, and uses keccak-wide only.

Separately, #14431 defers transactions before signature verification once the shared head budget is spent. That fixes wasted signature work on deferrals; it was not included in these measurements.

## Deliverable: modifiche proposte

### Notion di Manuel, “What protects the node”

- Distinguere flood controller, budget globale per head e guardie sperimentali per peer.
- #14394: gas/s, dipendente dalla taratura hardware. #14414: tempo per head, dati della tabella sopra. Non descriverle entrambe come fair share.
- #14431: fix di lavoro evitabile dopo budget esaurito; nessun benchmark attribuito al nuovo fix.
- Non aggiungere una seconda sezione “Mitigation tested”: usare la sezione esistente.

### Notion e testo di Daniil, “Limits”

Proposta pronta per inserimento:

> These are Nethermind measurements, with client-specific protections. Import and build pauses are enabled on the protected paths. The global per-head simulation budget is lifted for the EVM producer/import rows and enabled for honest-admission rows. The measured rate of roughly 100 rejected EVM transactions/s per peer comes from Nethermind's network handling, not EIP-8141. Per-peer gas/time budgets are separate prototype experiments; their results do not establish a higher protocol cap or equivalent protection in other clients.

### Testo di Daniil, “The setup”

- “Load shedding is off” è troppo generico: separare producer/import da honest admission.
- Il diagramma “VERIFY fails” copre il keccak, non il sig-stuffed, che spende recovery senza eseguire EVM.
- Esplicitare workload sintetico del producer e scaling al tempo reale del blocco.
- Attribuire i dati import alla macchina: “below about 1.3 ms at 30M” non comprende Low sig-stuffed, non sostenuto.
- Distinguere cap candidato per builder e perdita di admission misurata su Low. Evitare di chiamare n1 direttamente “full node”.

## Cosa resta

- [x] Analizzare il commento flcl42, riprodurre la regressione e pubblicare il fix su #14431.
- [x] Leggere il verdetto Claude su `55f9549338`: mergeable, nessun blocker. PR aperta ready, non mergiata.
- [ ] Far verificare le credenziali Docker Hub della CI; poi rieseguire i job EXPB falliti.
- [ ] Approvare/inviare la bozza sopra a Daniil, con i limiti preregistrati.
- [ ] Concordare gli inserimenti nei due deliverable; aggiornare Notion online solo dopo la revisione del testo.
- [ ] Aggiornare `00. Campaign overview.md`, ancora a v4 e con decisioni già superate, e i metadati draft dei deliverable.
- [ ] Prima della pubblicazione EF: fonti/run/commit pubblici, elenco delle protezioni per suite, limiti del singolo client.

Nessuna nuova campagna di misura proposta: producer con fair share e budget attivo, sig-stuffed fair share e Sybil restano esplicitamente fuori dai risultati disponibili.
