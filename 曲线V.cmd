@echo off
rem Switch the live driver curve to V: ONLY the vertical output multiplier.
rem   Y/X output DPI ratio: 1.0 to 1.25   (profile sha c63b44cfe7a0)
rem Nothing else changes. X stays offset 3.0 / accel 0.06 / cap 1.97, and the Y
rem accel curve stays offset 2.5 / accel 0.15 / cap 1.83.
rem
rem Why this exists: a Y ACCEL curve cannot help a spray. A spray pulls at
rem 0.38-0.50 counts/ms, and classic gain is 1 + accel * (v - offset)^2 / v, so
rem it is exactly 1 below the offset and only a few percent just above it. A
rem constant multiplier works at every speed, including a slow steady pull.
rem Effect: the game sees vertical sensitivity 1.5 as 1.875 while horizontal
rem sensitivity stays 1.5.
rem
rem House rules for every .cmd in this folder (checked by field/check-cmd.mjs):
rem  - ASCII only. cmd.exe reads these files in the OEM codepage, so UTF-8
rem    Chinese (even inside a file name in an echo) comes out as garbage whose
rem    bytes can decode to a caret and splice the next line into this one.
rem  - never put a bare arrow in an echo line: ">" is a REDIRECT. It truncated
rem    the line and silently created files named "1.25" and "1.875" here.
rem    Use the word "to", or escape it as "^>". A rem line is safe.
cd /d "%~dp0"
echo.
echo  ============================================================
echo   Switching curve to  V  =  vertical multiplier 1.25
echo  ============================================================
echo   One value changes: Y/X output DPI ratio 1.0 to 1.25.
echo   Aim at a wall: the same hand pull moves the crosshair 25 percent down.
echo   Your mouse may hitch for about a second while the driver takes the write.
echo   Do NOT press "write to driver" in the UI during a test: it would push
echo   the UI's own draft over whatever this script just set.
echo.
echo   The UI path (test page AI box) is usually better: it also stores the
echo   settings.json copy and has a one-click revert. See ai-patch-V.txt.
echo.
app\RaProbe.exe --apply-settings curves\V-yx1.25.json --yes
echo.
echo  If the line above says the read-back matched, V is live.
echo  Check any time with the read-only status script in this folder.
echo  Back to baseline with the A script in this folder.
echo.
echo  Do not judge this change with the spray-Y capture: that report divides
echo  the multiplier back out by definition, so it cannot confirm it. The proof
echo  the write worked is the read-back line above. Judge it in the aim map.
pause
