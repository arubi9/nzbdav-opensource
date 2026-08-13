export const updateFromInput = <Target extends object, Key extends keyof Target>(
  event: { currentTarget: Target },
  update: (value: Target[Key]) => void,
  key: Key,
): void => {
  const value = event.currentTarget[key];
  update(value);
};
