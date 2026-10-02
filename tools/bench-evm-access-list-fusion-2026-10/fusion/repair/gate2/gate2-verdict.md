GATE 1: FAIL
- regresje > +2% na stabilnych: BalanceRead T +3.0% (najgorsza para +4.9%, baza 8.943 / 9.055); CallData T +2.3% (najgorsza para +4.8%, baza 2.445 / 2.502); CallIdentity T +2.7% (najgorsza para +4.3%, baza 18.086 / 18.175); Environment F +3.4% (najgorsza para +4.8%, baza 5.910 / 5.950); Memory T +2.5% (najgorsza para +4.2%, baza 2.596 / 2.611); StaticIdentity F +4.5% (najgorsza para +7.4%, baza 11.466 / 11.439)

| Łańcuch | baza F / T (ns/op) | Δ 1 wątek F / T | Δ SMT F / T | baza bimodalna (1w) |
|---|---|---|---|---|
| AddMod | 6.160 / 6.172 | +0.1% / +0.1% | n/a |  |
| AddModZero | 2.139 / 2.154 | +0.6% / -0.1% | n/a |  |
| Arithmetic | 2.749 / 2.750 | -0.0% / +0.2% | n/a |  |
| BalanceColdExistingPair | 20.322 / 20.061 | +0.3% / +0.9% | n/a |  |
| BalanceColdPair | 19.176 / 19.568 | +1.0% / -0.4% | n/a |  |
| BalanceColdSingle | 26.401 / 26.977 | +0.2% / -2.0% | n/a | T |
| BalanceRead | 9.055 / 8.999 | -0.3% / +3.0% | n/a |  |
| Bitwise | 3.235 / 3.239 | -0.0% / +0.3% | n/a |  |
| Byte | 2.449 / 2.644 | -2.7% / -9.4% | n/a | F |
| CallData | 2.470 / 2.474 | +0.9% / +2.3% | n/a |  |
| CallDataMissing | 2.356 / 2.361 | +0.0% / +1.3% | n/a |  |
| CallDataPartial | 3.092 / 3.099 | -0.0% / +0.2% | n/a |  |
| CallEmpty | 6.198 / 6.169 | +0.3% / +0.3% | n/a |  |
| CallIdentity | 18.334 / 18.131 | -0.4% / +2.7% | n/a | F |
| CallInput | 24.047 / 24.400 | +5.8% / -0.2% | n/a | F, T |
| CallReturn | 23.030 / 23.171 | +2.0% / -0.4% | n/a |  |
| CallRevert | 26.143 / 26.291 | +0.3% / -1.3% | n/a |  |
| Clz | 4.745 / 4.755 | +0.1% / -0.0% | n/a |  |
| CompareBranch | 2.491 / 2.591 | -24.9% / -26.6% | n/a | T |
| Context | 1.927 / 1.913 | -2.4% / -0.9% | n/a | F |
| DivIsZero | 3.767 / 3.775 | +1.7% / +1.7% | n/a |  |
| DivOne | 3.317 / 3.239 | -9.5% / -7.2% | n/a | F |
| DivSmall | 3.399 / 3.405 | -0.1% / +0.0% | n/a |  |
| DivWide | 9.549 / 9.225 | -3.5% / +0.0% | n/a | F |
| DivZero | 2.428 / 2.355 | -17.5% / -13.7% | n/a | F |
| Environment | 5.930 / 6.002 | +3.4% / +1.7% | n/a |  |
| ExpCompute | 9.311 / 9.346 | +0.0% / -0.6% | n/a |  |
| ExpOne | 5.131 / 5.180 | -4.7% / -2.1% | n/a | T |
| ExtCodeSizeRead | 10.499 / 10.536 | +1.3% / +0.1% | n/a |  |
| JumpAlternating | 1.770 / 1.811 | -4.6% / +2.9% | n/a | F, T |
| JumpScattered | 1.782 / 1.777 | -14.2% / -7.5% | n/a | F, T |
| JumpScatteredPush3 | 2.256 / 2.182 | -7.0% / -2.0% | n/a |  |
| JumpScatteredRotating | 1.757 / 1.817 | -9.3% / -6.0% | n/a | T |
| JumpTaken | 1.975 / 1.905 | -38.1% / -31.9% | n/a | F, T |
| JumpUntaken | 1.765 / 1.657 | -31.0% / -26.0% | n/a | F, T |
| Memory | 2.593 / 2.603 | +0.2% / +2.5% | n/a |  |
| MemoryBoundary | 2.918 / 2.887 | -1.6% / -0.1% | n/a |  |
| MemoryByte | 2.810 / 2.768 | -1.9% / +0.8% | n/a |  |
| MemoryCopy | 5.135 / 5.104 | +0.6% / +1.1% | n/a |  |
| MemoryHash | 5.347 / 5.376 | -0.4% / -1.3% | n/a |  |
| ModOne | 2.501 / 2.356 | -14.2% / -8.3% | n/a | F |
| ModSmall | 3.334 / 3.335 | -0.1% / +0.1% | n/a |  |
| ModWide | 7.485 / 7.457 | -0.7% / +0.2% | n/a |  |
| ModZero | 2.499 / 2.357 | -13.7% / -8.3% | n/a | F |
| MulDup | 3.012 / 3.034 | +0.4% / -0.1% | n/a |  |
| MulMod | 5.233 / 5.240 | -0.1% / +0.0% | n/a |  |
| MulModZero | 2.142 / 2.218 | +0.1% / -2.2% | n/a |  |
| Predicate | 2.352 / 2.417 | -24.3% / -26.0% | n/a | T |
| PrevRandao | 2.291 / 2.367 | -23.5% / -25.2% | n/a | F |
| ReturnDataSize | 2.366 / 2.365 | -30.8% / -30.3% | n/a |  |
| Sar | 3.897 / 3.909 | +0.1% / -0.2% | n/a |  |
| SarAnd | 2.175 / 2.222 | -2.3% / -4.0% | n/a | F, T |
| SelfBalanceRead | 4.120 / 4.055 | -3.5% / +0.3% | n/a | F |
| Shift | 3.423 / 3.430 | -0.1% / +0.2% | n/a |  |
| SmallValue | 1.880 / 1.909 | -5.3% / -5.6% | n/a |  |
| Stack | 2.117 / 2.136 | +0.3% / -0.7% | n/a |  |
| StaticIdentity | 11.452 / 11.639 | +4.5% / -0.3% | n/a |  |
| StorageRead | 5.809 / 5.199 | -8.5% / +1.3% | n/a | F, T |
| StorageWrite | 7.638 / 9.089 | +3.3% / -11.2% | n/a | F, T |
| TransientRead | 3.264 / 3.278 | +0.5% / +0.5% | n/a |  |

Bimodalne (rozrzut dwóch przebiegów bazy >= 3%, 1 wątek):
- BalanceColdSingle T (baza 27.575 / 26.380, rozrzut 4.5%, Δ -2.0%)
- Byte F (baza 2.520 / 2.378, rozrzut 6.0%, Δ -2.7%)
- CallIdentity F (baza 18.666 / 18.001, rozrzut 3.7%, Δ -0.4%)
- CallInput F (baza 24.509 / 23.585, rozrzut 3.9%, Δ +5.8%)
- CallInput T (baza 24.030 / 24.769, rozrzut 3.1%, Δ -0.2%)
- CompareBranch T (baza 2.516 / 2.665, rozrzut 5.9%, Δ -26.6%)
- Context F (baza 1.970 / 1.885, rozrzut 4.5%, Δ -2.4%)
- DivOne F (baza 3.387 / 3.246, rozrzut 4.3%, Δ -9.5%)
- DivWide F (baza 9.210 / 9.889, rozrzut 7.4%, Δ -3.5%)
- DivZero F (baza 2.543 / 2.312, rozrzut 10.0%, Δ -17.5%)
- ExpOne T (baza 5.278 / 5.081, rozrzut 3.9%, Δ -2.1%)
- JumpAlternating F (baza 1.810 / 1.730, rozrzut 4.6%, Δ -4.6%)
- JumpAlternating T (baza 1.773 / 1.850, rozrzut 4.3%, Δ +2.9%)
- JumpScattered F (baza 1.921 / 1.643, rozrzut 16.9%, Δ -14.2%)
- JumpScattered T (baza 1.651 / 1.902, rozrzut 15.2%, Δ -7.5%)
- JumpScatteredRotating T (baza 1.762 / 1.871, rozrzut 6.2%, Δ -6.0%)
- JumpTaken F (baza 2.038 / 1.912, rozrzut 6.6%, Δ -38.1%)
- JumpTaken T (baza 1.822 / 1.988, rozrzut 9.1%, Δ -31.9%)
- JumpUntaken F (baza 1.802 / 1.728, rozrzut 4.3%, Δ -31.0%)
- JumpUntaken T (baza 1.580 / 1.734, rozrzut 9.8%, Δ -26.0%)
- ModOne F (baza 2.650 / 2.352, rozrzut 12.7%, Δ -14.2%)
- ModZero F (baza 2.643 / 2.354, rozrzut 12.2%, Δ -13.7%)
- Predicate T (baza 2.330 / 2.504, rozrzut 7.5%, Δ -26.0%)
- PrevRandao F (baza 2.363 / 2.218, rozrzut 6.6%, Δ -23.5%)
- SarAnd F (baza 2.210 / 2.139, rozrzut 3.3%, Δ -2.3%)
- SarAnd T (baza 2.300 / 2.143, rozrzut 7.3%, Δ -4.0%)
- SelfBalanceRead F (baza 4.011 / 4.229, rozrzut 5.4%, Δ -3.5%)
- StorageRead F (baza 5.147 / 6.471, rozrzut 25.7%, Δ -8.5%)
- StorageRead T (baza 5.088 / 5.309, rozrzut 4.3%, Δ +1.3%)
- StorageWrite F (baza 7.495 / 7.780, rozrzut 3.8%, Δ +3.3%)
- StorageWrite T (baza 10.279 / 7.899, rozrzut 30.1%, Δ -11.2%)

Bilans 1 wątek: 40 lepszych o >2%, 71 neutralnych, 9 gorszych o >2% (z 120).
Bilans SMT: 0 lepszych o >2%, 0 neutralnych, 0 gorszych o >2% (z 0).
