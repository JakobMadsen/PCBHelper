# Designspecifikation: 10 kHz Differentiel Fotodiode-Modtager

## Formaal
Dette dokument beskriver den tekniske specifikation og arkitektur for et differentielt fotodiode-modtagerkredsloeb optimeret til et 10 kHz moduleret sinus-lyssignal.

Maalet er, at specifikationen kan bruges direkte af mennesker og af automatisering via GitHub Copilot eller MCP-vaerktoejer til opbygning i KiCad.

## 1. Systemarkitektur og signalflow
Kredsloebet er opdelt i fire primare analoge blokke for at opnaa hoej foelsomhed, undertrykkelse af DC-baggrundslys og lav stoepaafoelsomhed.

1. Input-trin (anti-parallel TIA)
- To fotodioder kobles back-to-back ind i en transimpedansforstaerker.
- Differentiel lysstroem subtraheres analogt i inputknuden.

2. AC-kobling (hoejpas)
- Rest-DC og lavfrekvent stoj fjernes efter TIA-trinnet.
- Typisk stoekilder: 50 Hz og 100 Hz netstoerelser.

3. Aktivt baandpasfilter (10 kHz)
- Multiple Feedback (MFB) topologi.
- Smalbandoet filtrering centreret omkring 10 kHz.

4. Fasedetektion / demodulation
- Output-amplitude angiver differensens stoerrelse.
- Fase (0 grader eller 180 grader) angiver hvilken diode der modtager stoerst signal.

## 2. Tekniske parametre og komponentvaerdier

| Parameter / blok | Specifikation / vaerdi | Funktionel beskrivelse |
|---|---|---|
| Moduleringsfrekvens | 10 kHz (sinus) | Baerefrekvens for lyskilden |
| Fotodiode-konfiguration | Anti-parallel / back-to-back | PD1 anode + PD2 katode til op-amp inverterende input. Giver oejeblikkelig analog subtraktion foer spaendingsforstaerkning |
| TIA feedback-modstand (Rf) | 100 kohm til 1 Mohm | Bestemmer AC-forstaerkning af stroemdifferensen. Justeres efter forventet lysintensitet |
| TIA feedback-kondensator (Cf) | 10 pF til 22 pF | Sikrer lukket-sløjfe stabilitet og kompenserer for fotodiodernes parasitaere kapacitans ($C_d$) |
| AC-koblingskondensator (Cac) | 100 nF | Serie-kobles efter TIA-trinnet for DC-blokering |
| Aktivt baandpasfilter | MFB, Q = 10 | Smalbandoet filtrering omkring 10 kHz for stoajreduktion |
| Anbefalet op-amp | OPA2325 eller OPA2388 (dual) | Lav input-biasstroem og lav spaendingsstoej, egnet til svage fotostroemme |

## 3. Detaljerede forbindelsesinstruktioner til KiCad

### Trin A: TIA front-end (U1A)
- PD1: anode til U1A pin 2 (inverterende), katode til GND.
- PD2: katode til U1A pin 2 (inverterende), anode til GND.
- Feedback-sloejfe: Rf og Cf i parallel mellem U1A pin 2 og U1A pin 1 (udgang).
- Reference:
  - Dual-supply: U1A pin 3 (ikke-inverterende) til GND.
  - Single-supply: U1A pin 3 til stabil virtuel reference, fx VCC/2.

### Trin B: AC-kobling og filter (U1B)
- Forbind U1A pin 1 til Cac ind.
- Cac ud foeres til input-modstandsnet i MFB-baandpas.
- Etabler MFB-topologi omkring U1B ved at forbinde beregnede R/C-led til:
  - U1B pin 6 (inverterende input)
  - U1B pin 7 (udgang)
- U1B pin 5 (ikke-inverterende input) forbindes til samme reference som U1A pin 3.

## 4. Designkrav for implementering

### Elektriske krav
- Centerfrekvens: 10 kHz.
- MFB Q-faktor: 10 (maalrettet smalbandoet respons).
- TIA skal vaere stabil med valgt diodekapacitans og layoutparasitics.
- AC-kobling skal blokere DC-offset uden at daempe 10 kHz vaesentligt.

### Layoutkrav
- Minimer ledningslaengde mellem PD1/PD2 og U1A inverterende input.
- Hold feedback-sloejfen (Rf, Cf) kompakt og taet paa U1A.
- Brug solid referencejord omkring analog front-end.
- Undgaa stoerfanger-loekker i input- og filteromraade.

### Verifikationskrav
- ERC skal vaere uden kritiske fejl.
- DRC skal vaere uden shorts, unconnected items og dangling tracks/vias.
- Filterrespons skal verificeres omkring 10 kHz i simulation eller bench-test.

## 5. MCP/Scripting-struktur til KiCad (eksempel)
Foelgende struktur kan bruges som udgangspunkt for automatiseret skemagenerering:

```text
# Initialisering af diagram
create_schematic --title "10kHz_Differential_Receiver"

# Primaere komponenter
add_component --symbol "Device:D_Photo" --designator "PD1"
add_component --symbol "Device:D_Photo" --designator "PD2"
add_component --symbol "Amplifier_Operational:OPA2325" --designator "U1"
add_component --symbol "Device:R" --designator "Rf"
add_component --symbol "Device:C" --designator "Cf"
add_component --symbol "Device:C" --designator "Cac"

# Differentielt input
connect_pins "PD1:1 U1:2"
connect_pins "PD2:2 U1:2"
connect_pins "PD1:2 GND"
connect_pins "PD2:1 GND"

# TIA feedback
connect_pins "Rf:1 U1:2"
connect_pins "Rf:2 U1:1"
connect_pins "Cf:1 U1:2"
connect_pins "Cf:2 U1:1"

# AC-kobling videre til filter
connect_pins "U1:1 Cac:1"
```

## 6. Aabne afklaringer foer PCB release
- Endelig valg af op-amp: OPA2325 vs OPA2388 baseret paa stoaj, forsyning og tilgaengelighed.
- Endelige MFB-vaerdier (R/C) efter oensket baandbredde og gain.
- Single-supply eller dual-supply drift og referencearkitektur.
- Testplan for fasebestemmelse (0 grader / 180 grader) i demodulationsblok.

## 7. Klar-til-ordre kriterier (PCB + komponenter)
Foer bestilling hos fx PCBWay skal foelgende vaere opfyldt:

1. Skema og PCB er synkroniseret uden netkonflikter.
2. ERC og DRC er uden blokerende fejl.
3. Gerber + drill + BOM + position files er eksporteret.
4. BOM indeholder entydige MPN-numre for alle montagede komponenter.
5. Position files (CPL) har korrekt origin, side og rotation.
6. Manufacturing-pakke er verificeret og arkiveret i release-folder.
