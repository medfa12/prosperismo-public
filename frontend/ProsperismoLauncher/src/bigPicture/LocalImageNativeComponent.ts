/* eslint-disable @react-native/no-deep-imports -- codegenNativeComponent is exposed through RN's internal utility path. */
import type {ViewProps} from 'react-native';
import type {Double} from 'react-native/Libraries/Types/CodegenTypes';
import codegenNativeComponent from 'react-native/Libraries/Utilities/codegenNativeComponent';

interface LocalImageProps extends ViewProps {
  path: string;
  contain: boolean;
  displayWidth: Double;
  displayHeight: Double;
  tintRed: Double;
  tintGreen: Double;
  tintBlue: Double;
}

/** WIC-backed local PNG renderer used where RNW Fabric's file Image stalls. */
export default codegenNativeComponent<LocalImageProps>('ProsperismoLocalImage');
