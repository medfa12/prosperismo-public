import {Platform} from 'react-native';
import {
  resetShellNativeComponents,
  shellFocusRingComponent,
  shellLocalImageComponent,
} from '../src/bigPicture/nativeShellComponents';

describe('guarded shell native components', () => {
  beforeEach(() => resetShellNativeComponents());

  it('resolves to null on a non-Windows host', () => {
    expect(Platform.OS).not.toBe('windows');
    expect(shellFocusRingComponent()).toBeNull();
    expect(shellLocalImageComponent()).toBeNull();
  });

  it('never evaluates the codegen modules off Windows', () => {
    // codegenNativeComponent throws when the native view is unregistered, so
    // the guard has to short-circuit before the require - not merely catch.
    // Importing the home shell must therefore stay side-effect free here.
    expect(() => {
      shellFocusRingComponent();
      shellLocalImageComponent();
    }).not.toThrow();
    expect(jest.isMockFunction(shellFocusRingComponent)).toBe(false);
  });

  it('caches the resolution', () => {
    expect(shellFocusRingComponent()).toBe(shellFocusRingComponent());
    expect(shellLocalImageComponent()).toBe(shellLocalImageComponent());
  });

  it('the home shell imports without a native host present', () => {
    // The regression this locks in: FocusRing and LocalImage used to be
    // imported statically, so requiring the shell evaluated
    // codegenNativeComponent unconditionally and made the tree unrenderable
    // anywhere but Windows.
    expect(() => require('../src/bigPicture/RecoveredHomeShell')).not.toThrow();
    expect(() => require('../src/bigPicture/ShellFocusOverlay')).not.toThrow();
  });
});
