# Stack #14394 / #14414: aggiornamento 2026-10-08

## Pubblicazione

- Master di riferimento: `13fa0cac51eb75ba8a1ec53ca69140e2329226fb`.
- #14394: `0e5d833e8e51fd043bd3e8cd60a48db81d0a3ef8`, ready.
- #14414: `fb1c0ab6f86a126bd3d7b67a79e60a260d72949d`, ready for review.
- Push atomico, lease sugli head precedenti.
- Sei commit del padre identici nel range-diff del rebase.
- Storia della figlia consolidata: diff completo conservato, poi due commit separati per isolamento del test e mitigazione.

## Follow-up di Ben

- [#14394](https://github.com/NethermindEth/nethermind/pull/14394#pullrequestreview-5455471687): clock fermo nei due test Eth72 sul budget, tramite sottotipo di test esistente. Nessuna API di produzione aggiunta.
- Scope multipli rimossi dai due test di budget con mutazioni dipendenti.
- Verifica padre: 182 test Eth72 e 7 test budget passati; build e formattazione pass.
- [#14414](https://github.com/NethermindEth/nethermind/pull/14414#pullrequestreview-5455472217): scope rimosso da `HasShare_ASecondActivePeer_HalvesTheShare`.
- Merge del padre nella figlia, senza force push. Conservata la rimozione del budget a gas e dei relativi test.
- Verifica figlia: 7 test quote passati; build e formattazione pass. Codice di produzione identico al precedente head.

## Daniil: mitigazione implementata

- [Commento](https://github.com/NethermindEth/nethermind/pull/14414#discussion_r4214252252).
- Prima: tabella vuota a ogni head, primo peer con quota iniziale intera.
- Ora: divisore = massimo tra peer del head corrente e peer attivi nel head immediatamente precedente.
- Conservato soltanto il conteggio precedente, senza riferimenti ai vecchi handler.
- Peer inattivi: scadenza dopo un head.
- Head saltati: conteggio precedente scartato.
- Primo head osservato: nessun conteggio storico.

## Verifica deterministica

Budget del test: 1.000 tick. Soglie di ammissione, escluso overshoot della validazione già iniziata.

| Scenario | Prima | Dopo |
|---|---:|---:|
| Due peer attivi nel head precedente, attaccante arriva per primo | 1.000 | 500 |
| Il peer onesto resta inattivo per un head, head successivo con un solo peer | 1.000 | 1.000 |
| Un head senza submission tra le due osservazioni | 1.000 | 1.000 |

- Regressione prima della modifica: il caso da 500 tick fallisce, quota ancora disponibile.
- Dopo: sette test di accounting passano.
- Coperti anche gli addebiti tardivi, che non devono azzerare il divisore storico.
- Nessuna misura nuova di CPU o admission rate su hardware di benchmark.

## Test finali

- Build TxPool.Test e Network.Test: 55 progetti, zero errori e warning.
- TxPool: 1.637 pass, 45 skip. Due run completi consecutivi riusciti.
- Eth72: 175 pass.
- Whitespace e diff-check: pass.
- Metrica `PendingTransactionsDiscarded`: i due casi restano invariati, spostati in un fixture non parallelo allo stesso livello degli altri fixture. Il fixture annidato non isolava il contatore dai test del contenitore.

## Limiti e stato della review

- #14414 pronta per review. Misure di workload con arrivi sfalsati e churn ancora da eseguire.
- Submission concorrenti: overshoot fino a una validazione per submission in volo.
- Late arrivals: tempo già consumato non recuperabile.
- Più connessioni possono ottenere più quote.
- Peer inattivi riducono temporaneamente la quota per un head.
- Attese del sender admission gate dentro i filtri incluse nel tempo addebitato.
- Riferimenti ai peer correnti mantenuti fino al rollover; churn su head fermo non risolto.
- Review Claude di #14394: zero findings sul vecchio head `919dc227`, precedente ai rebase.
- Nessuna nuova review umana pubblicata.
- [Risposta a Daniil pubblicata](https://github.com/NethermindEth/nethermind/pull/14414#discussion_r4217595640).
- [Claude sul nuovo head](https://github.com/NethermindEth/nethermind/pull/14414#issuecomment-6057636943): mergeable, zero Critical/High/Medium. Low residui: submission a cavallo della head e riferimenti trattenuti fino al rollover. Nota di design sulle connessioni Sybil.
