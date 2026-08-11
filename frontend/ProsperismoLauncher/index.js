/**
 * @format
 */

import React from 'react';
import { AppRegistry, Text, View } from 'react-native';
import { name as appName } from './app.json';

const failureStyle = {
  root: { flex: 1, backgroundColor: '#020408', padding: 48, justifyContent: 'center' },
  title: { color: '#ffffff', fontSize: 30, fontWeight: '600', marginBottom: 18 },
  body: { color: 'rgba(255,255,255,0.72)', fontSize: 18, lineHeight: 26 },
};

function FailureSurface({error}) {
  const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
  return React.createElement(
    View,
    {style: failureStyle.root},
    React.createElement(Text, {style: failureStyle.title}, 'The shell could not start'),
    React.createElement(Text, {style: failureStyle.body}, message),
  );
}

class RootErrorBoundary extends React.Component {
  state = {error: undefined};

  static getDerivedStateFromError(error) {
    return {error};
  }

  render() {
    return this.state.error
      ? React.createElement(FailureSurface, {error: this.state.error})
      : this.props.children;
  }
}

let RootComponent;
let moduleLoadError;
try {
  // Keep the application import inside the guard: failures in a shell module's
  // top-level native lookup otherwise leave RNW's compositor silently white.
  RootComponent = require('./App').default;
} catch (error) {
  moduleLoadError = error;
}

function RegisteredRoot() {
  if (moduleLoadError) {
    return React.createElement(FailureSurface, {error: moduleLoadError});
  }
  return React.createElement(
    RootErrorBoundary,
    null,
    React.createElement(RootComponent),
  );
}

AppRegistry.registerComponent(appName, () => RegisteredRoot);
