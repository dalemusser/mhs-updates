mergeInto(LibraryManager.library, {

  // ---------------------------------------------------------------------------
  // MHSBridge JavaScript Plugin (jslib)
  //
  // This plugin provides the browser-side functions that MHSBridge.cs calls
  // via [DllImport("__Internal")].
  //
  // Identity and service configuration come from window.__mhsBridgeConfig,
  // which is set by the host page before Unity starts. If the config is
  // absent, identity and service config are not available from the bridge.
  // ---------------------------------------------------------------------------

  // Called by C# to get the full bridge config as a JSON string.
  // Returns the serialized window.__mhsBridgeConfig, or empty string if absent.
  MHSBridge_GetConfig: function() {
    var json = '';
    if (typeof window.__mhsBridgeConfig !== 'undefined' && window.__mhsBridgeConfig !== null) {
      try {
        json = JSON.stringify(window.__mhsBridgeConfig);
      } catch (e) {
        json = '';
      }
    }
    var bufferSize = lengthBytesUTF8(json) + 1;
    var buffer = _malloc(bufferSize);
    stringToUTF8(json, buffer, bufferSize);
    return buffer;
  },

  // Called by C# to free any _malloc'd pointer returned by this plugin.
  MHSBridge_Free: function(ptr) {
    if (ptr) {
      _free(ptr);
    }
  },

  // Called by C# to notify the PWA that a unit has been completed.
  // unitIdPtr: pointer to a C string containing the unit ID (e.g., "unit3")
  MHSBridge_NotifyUnitComplete: function(unitIdPtr) {
    var unitId = UTF8ToString(unitIdPtr);
    if (typeof window.mhsUnitComplete === 'function') {
      window.mhsUnitComplete(unitId);
    }
  },

  // Called by C# to signal that the game has ended (player finished all content).
  // In PWA mode: calls window.mhsEndGame() which navigates back to StrataHub.
  // In non-PWA mode: tries to close the tab, shows a message if the browser blocks it.
  MHSBridge_EndGame: function() {
    if (typeof window.mhsEndGame === 'function') {
      window.mhsEndGame();
    } else {
      // Non-PWA fallback: try to close the tab
      window.close();
      // If window.close() was blocked by the browser, show a message
      setTimeout(function() {
        document.body.innerHTML =
          '<div style="display:flex;align-items:center;justify-content:center;height:100%;background:#1a1a2e;color:#e0e0e0;font-family:system-ui,sans-serif;">' +
          '<div style="text-align:center;max-width:400px;">' +
          '<h1 style="font-size:1.5rem;margin-bottom:1rem;">Game Complete</h1>' +
          '<p>You can close this tab now.</p>' +
          '</div></div>';
      }, 500);
    }
  },

  // Called by C# to navigate to a unit URL.
  // In managed mode (unitMap), this receives a full path or URL.
  // In relative mode, this receives a relative URL like "../unit2/index.html".
  // Does NOT carry URL parameters forward (identity is no longer in the URL).
  MHSBridge_NavigateToUnit: function(urlPtr) {
    var url = UTF8ToString(urlPtr);
    // Resolve relative URL against current page
    var resolved = new URL(url, window.location.href);
    window.location.href = resolved.href;
  },

  // Called by C# to get a unit URL by name.
  // If a unitMap is present in the config, looks up the unit there:
  //   - Returns the URL string if the unit is available
  //   - Returns "LOCKED" if the unit is explicitly locked (null in unitMap)
  // If no unitMap exists, returns a relative URL (e.g., "../unit2/index.html").
  MHSBridge_GetUnitURL: function(unitNamePtr) {
    var unitName = UTF8ToString(unitNamePtr);
    var result = '';

    if (typeof window.__mhsBridgeConfig !== 'undefined' &&
        window.__mhsBridgeConfig !== null &&
        window.__mhsBridgeConfig.navigation &&
        window.__mhsBridgeConfig.navigation.unitMap) {
      var unitMap = window.__mhsBridgeConfig.navigation.unitMap;
      if (unitName in unitMap) {
        if (unitMap[unitName] === null) {
          result = 'LOCKED';
        } else {
          result = unitMap[unitName];
        }
      }
    } else {
      // No unitMap — return a relative URL
      result = '../' + unitName + '/index.html';
    }

    var bufferSize = lengthBytesUTF8(result) + 1;
    var buffer = _malloc(bufferSize);
    stringToUTF8(result, buffer, bufferSize);
    return buffer;
  }

});
