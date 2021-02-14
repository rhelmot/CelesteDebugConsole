#!/bin/bash -e

msbuild
VERSION=$(cat DebugConsole/everest.yaml | grep '^  Version' | cut -d' ' -f 4)
FILENAME=dist/DebugConsole_${VERSION}${2}.zip
rm -f $FILENAME
cd DebugConsole/bin/${1-Debug}
zip -r ../../../${FILENAME} *
echo Finished in ${FILENAME}
